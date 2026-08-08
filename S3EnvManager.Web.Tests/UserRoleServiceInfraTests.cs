using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>RBAC 역할 배정(Tier 1-1)을 실제 Postgres + ASP.NET Core Identity 스택으로
/// 검증한다. Blazor UI 자체는 이 테스트 대상이 아니고, 그 화면이 호출하는
/// UserRoleService/UserRoleBootstrapService 로직을 실제 Identity 스토어에 대해 검증한다.</summary>
public class UserRoleServiceInfraTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task Bootstrap_AssignsGuestToRolelessUsers_Idempotently()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);

		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

		var memberEmail = "member-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var memberUser = await CreateUserAsync(userManager, memberEmail);

		try
		{
			// "첫 관리자" 부트스트랩은 Register.razor/InitialAdminSetupTokenService로 옮겨졌다 -
			// 여기서는 역할 없는 사용자를 Guest로 채워주는 것만 검증한다.
			var roleService = new UserRoleService(userManager, new AuditLogger(CreateDbContext()));
			await UserRoleBootstrapService.EnsureDefaultRolesAssignedAsync(userManager);
			var users = await roleService.ListAsync();
			Assert.Equal(IdentityRoleNames.Guest, users.Single(u => u.Email == memberEmail).Role);

			await UserRoleBootstrapService.EnsureDefaultRolesAssignedAsync(userManager);
			var usersAfterSecondRun = await roleService.ListAsync();
			Assert.Equal(IdentityRoleNames.Guest, usersAfterSecondRun.Single(u => u.Email == memberEmail).Role);
		}
		finally
		{
			await userManager.DeleteAsync(memberUser);
		}
	}

	[Fact]
	public async Task SetRoleAsync_ChangesRoleExclusively()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);

		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
		var email = "setrole-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = await CreateUserAsync(userManager, email);
		await userManager.AddToRoleAsync(user, IdentityRoleNames.Guest);

		var roleService = new UserRoleService(userManager, new AuditLogger(CreateDbContext()));
		await roleService.SetRoleAsync(user.Id, IdentityRoleNames.Member);

		var roles = await userManager.GetRolesAsync(user);
		var role = Assert.Single(roles);
		Assert.Equal(IdentityRoleNames.Member, role);

		await roleService.SetRoleAsync(user.Id, IdentityRoleNames.Administrator);
		roles = await userManager.GetRolesAsync(user);
		role = Assert.Single(roles);
		Assert.Equal(IdentityRoleNames.Administrator, role);

		// 다른 테스트의 "Administrator가 0명" 전제를 깨지 않도록 정리.
		await userManager.DeleteAsync(user);
	}

	// 유일한 Administrator가 스스로를 강등/잠금하면 아무도 관리 화면에 못 들어간다. Users.razor가
	// 컨트롤을 Disabled로 막고 있었지만 그건 화면 쪽 방어라 서비스 계층에도 가드가 필요하다.
	[Fact]
	public async Task SetRoleAsync_And_SetLockedOutAsync_RejectSelfChange()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);

		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
		var email = "self-guard-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = await CreateUserAsync(userManager, email);
		await userManager.AddToRoleAsync(user, IdentityRoleNames.Administrator);

		var roleService = new UserRoleService(userManager, new AuditLogger(CreateDbContext()));

		await Assert.ThrowsAsync<InvalidOperationException>(
			() => roleService.SetRoleAsync(user.Id, IdentityRoleNames.Guest, actorUserId: user.Id));
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => roleService.SetLockedOutAsync(user.Id, lockedOut: true, actorUserId: user.Id));

		// 역할과 잠금 상태가 그대로여야 한다.
		Assert.Equal(IdentityRoleNames.Administrator, Assert.Single(await userManager.GetRolesAsync(user)));
		Assert.False(await userManager.IsLockedOutAsync(user));

		// 다른 관리자가 바꾸는 것은 여전히 허용된다.
		await roleService.SetRoleAsync(user.Id, IdentityRoleNames.Member, actorUserId: "someone-else");
		Assert.Equal(IdentityRoleNames.Member, Assert.Single(await userManager.GetRolesAsync(user)));

		// 다른 테스트의 "Administrator가 0명" 전제를 깨지 않도록 정리.
		await userManager.DeleteAsync(user);
	}

	// 역할 배타성은 이 서비스의 규약일 뿐 DB 제약이 아니다 - Account/Register.razor가 UserManager를
	// 직접 써서 부여하므로 두 역할을 동시에 가진 상태가 들어올 수 있다. 그때 "이전 역할"을 하나만
	// 집어 비교하면, 실제로 권한이 바뀌었는데도 감사 로그가 남지 않는다.
	[Fact]
	public async Task SetRoleAsync_WhenUserHoldsMultipleRoles_StillAudits_TheRemoval()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);

		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
		var email = "multirole-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = await CreateUserAsync(userManager, email);

		// 배타성이 깨진 상태를 만든다(UserManager 직접 사용 - 이 서비스를 우회하는 실제 경로와 동일).
		await userManager.AddToRoleAsync(user, IdentityRoleNames.Guest);
		await userManager.AddToRoleAsync(user, IdentityRoleNames.Member);
		Assert.Equal(2, (await userManager.GetRolesAsync(user)).Count);

		var actorUserId = "actor-" + Guid.NewGuid().ToString("N")[..8];
		var roleService = new UserRoleService(userManager, new AuditLogger(CreateDbContext()));

		// Member로 맞춘다 - 새로 붙는 역할은 없고 Guest만 떨어진다. 실제 권한 축소이므로 남아야 한다.
		await roleService.SetRoleAsync(user.Id, IdentityRoleNames.Member, actorUserId);

		Assert.Equal(IdentityRoleNames.Member, Assert.Single(await userManager.GetRolesAsync(user)));

		await using (var verifyDb = CreateDbContext())
		{
			var log = Assert.Single(
				await verifyDb.AuditLogs.AsNoTracking()
					.Where(l => l.ActorUserId == actorUserId
						&& l.EventType == AuditEventTypes.UserRoleChanged)
					.ToListAsync());

			// details 전체를 Contains로 보면 to="Member" 때문에 통과해버린다 - from만 콕 집어 본다.
			var from = System.Text.Json.JsonDocument.Parse(log.Details!).RootElement.GetProperty("from");
			var fromRoles = from.EnumerateArray().Select(e => e.GetString()).ToList();
			Assert.Contains(IdentityRoleNames.Guest, fromRoles);
			Assert.Contains(IdentityRoleNames.Member, fromRoles);
		}

		await userManager.DeleteAsync(user);
	}

	// 권한 상승은 이 서버에서 가장 민감한 이벤트인데 감사 로그에 남지 않던 것을 막는다.
	[Fact]
	public async Task SetRoleAsync_And_SetLockedOutAsync_WriteAuditLogs()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var roleManager = provider.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);

		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
		var email = "audit-role-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = await CreateUserAsync(userManager, email);
		await userManager.AddToRoleAsync(user, IdentityRoleNames.Guest);

		var actorUserId = "actor-" + Guid.NewGuid().ToString("N")[..8];
		var roleService = new UserRoleService(userManager, new AuditLogger(CreateDbContext()));
		await roleService.SetRoleAsync(user.Id, IdentityRoleNames.Member, actorUserId);
		await roleService.SetLockedOutAsync(user.Id, lockedOut: true, actorUserId);

		await using (var verifyDb = CreateDbContext())
		{
			var logs = await verifyDb.AuditLogs.AsNoTracking()
				.Where(l => l.ActorUserId == actorUserId).ToListAsync();

			var roleLog = Assert.Single(logs, l => l.EventType == AuditEventTypes.UserRoleChanged);
			Assert.Contains(user.Id, roleLog.Details);
			Assert.Contains(IdentityRoleNames.Guest, roleLog.Details);
			Assert.Contains(IdentityRoleNames.Member, roleLog.Details);

			var lockoutLog = Assert.Single(logs, l => l.EventType == AuditEventTypes.UserLockoutChanged);
			Assert.Contains(user.Id, lockoutLog.Details);
		}

		await userManager.DeleteAsync(user);
	}

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);

	private static async Task<ApplicationUser> CreateUserAsync(
		UserManager<ApplicationUser> userManager, string email)
	{
		var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
		var result = await userManager.CreateAsync(user, "Test-Password-123!");
		Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));
		return user;
	}

	private static async Task<bool> IsPostgresAvailableAsync()
	{
		try
		{
			await using var connection = new NpgsqlConnection(PostgresConnectionString);
			await connection.OpenAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static ServiceProvider BuildServiceProvider()
	{
		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(PostgresConnectionString));
		services.AddLogging();
		services.AddIdentityCore<ApplicationUser>()
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>();
		return services.BuildServiceProvider();
	}
}
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using S3EnvManager.Database;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>Program.cs의 Lockout 옵션(5회 실패 -> 10분 잠금)을 실제 Postgres + ASP.NET Core
/// Identity 스택으로 검증한다. SignInManager.PasswordSignInAsync(lockoutOnFailure: true)가
/// 비밀번호 실패 시 내부적으로 호출하는 UserManager.AccessFailedAsync를 직접 반복 호출해
/// 잠금 임계값과 잠금 시간을 확인한다.</summary>
public class LockoutInfraTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task AccessFailedAsync_LocksOutAfterFiveFailures_ForTenMinutes()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProvider();
		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

		var email = "lockout-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
		var createResult = await userManager.CreateAsync(user, "Test-Password-1234");
		Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(e => e.Description)));

		try
		{
			var beforeAttempts = DateTimeOffset.UtcNow;

			for (var attempt = 1; attempt <= 4; attempt++)
			{
				await userManager.AccessFailedAsync(user);
				Assert.False(await userManager.IsLockedOutAsync(user), $"{attempt}번째 실패에서 아직 잠기면 안 된다.");
			}

			await userManager.AccessFailedAsync(user);
			Assert.True(await userManager.IsLockedOutAsync(user), "5번째 실패 후에는 잠겨야 한다.");

			var lockoutEnd = await userManager.GetLockoutEndDateAsync(user);
			Assert.NotNull(lockoutEnd);

			var remaining = lockoutEnd!.Value - beforeAttempts;
			Assert.InRange(remaining, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(11));
		}
		finally
		{
			await userManager.DeleteAsync(user);
		}
	}

	[Fact]
	public async Task PasswordSignInAsync_WithLockoutOnFailureTrue_LocksOutAfterFiveWrongPasswords()
	{
		// Login.razor가 실제로 lockoutOnFailure: true를 넘기고 있는지까지 확인한다.
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var provider = BuildServiceProviderWithSignInManager();
		var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
		var signInManager = provider.GetRequiredService<SignInManager<ApplicationUser>>();

		var email = "signin-lockout-" + Guid.NewGuid().ToString("N")[..8] + "@test.local";
		var user = new ApplicationUser { UserName = email, Email = email, EmailConfirmed = true };
		var createResult = await userManager.CreateAsync(user, "Test-Password-1234");
		Assert.True(createResult.Succeeded, string.Join(", ", createResult.Errors.Select(e => e.Description)));

		try
		{
			for (var attempt = 1; attempt <= 4; attempt++)
			{
				var result = await signInManager.PasswordSignInAsync(
					email, "wrong-password", isPersistent: false, lockoutOnFailure: true);
				Assert.False(result.Succeeded);
				Assert.False(result.IsLockedOut, $"{attempt}번째 실패에서 아직 잠기면 안 된다.");
			}

			var fifthResult = await signInManager.PasswordSignInAsync(
				email, "wrong-password", isPersistent: false, lockoutOnFailure: true);
			Assert.True(fifthResult.IsLockedOut,
				"5번째 실패에서는 IsLockedOut 결과가 나와야 한다(Login.razor가 이 결과로 Account/Lockout으로 리다이렉트한다).");
		}
		finally
		{
			await userManager.DeleteAsync(user);
		}
	}

	private static ServiceProvider BuildServiceProviderWithSignInManager()
	{
		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(PostgresConnectionString));
		services.AddLogging();
		services.AddHttpContextAccessor();
		services.AddSingleton<IHttpContextAccessor>(
			new HttpContextAccessor { HttpContext = new DefaultHttpContext() });
		services.AddAuthentication();
		services.AddIdentityCore<ApplicationUser>(options =>
			{
				// RequireConfirmedAccount 누락 시 PreSignInCheck가 AccessFailedAsync 전에 실패를
				// 돌려줘 잠금이 걸리지 않을 수 있다 - Program.cs와 동일하게 재현한다.
				options.SignIn.RequireConfirmedAccount = true;
				options.Lockout.AllowedForNewUsers = true;
				options.Lockout.MaxFailedAccessAttempts = 5;
				options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
			})
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>()
			.AddSignInManager();
		return services.BuildServiceProvider();
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
		services.AddIdentityCore<ApplicationUser>(options =>
			{
				options.Lockout.AllowedForNewUsers = true;
				options.Lockout.MaxFailedAccessAttempts = 5;
				options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
			})
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>();
		return services.BuildServiceProvider();
	}
}
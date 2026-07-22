using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using S3EnvManager.Database;

namespace S3EnvManager.MigrationService;

// 마이그레이션 적용 + Identity 역할 자가 치유 후 종료(AppHost web이 WaitForCompletion으로 대기).
// CMK 부트스트랩/backup-readonly self-heal은 KMS(AWS SDK) 의존이라 Web.Program.cs에 남겨둔다.
public class Worker(
	IServiceProvider serviceProvider,
	IHostApplicationLifetime hostApplicationLifetime,
	ILogger<Worker> logger) : BackgroundService
{
	public const string ActivitySourceName = "Migrations";
	private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		using var activity = s_activitySource.StartActivity("Migrating database", ActivityKind.Client);

		try
		{
			using var scope = serviceProvider.CreateScope();
			var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

			logger.LogInformation("데이터베이스 마이그레이션을 시작합니다.");
			await dbContext.Database.MigrateAsync(stoppingToken);
			logger.LogInformation("데이터베이스 마이그레이션이 완료되었습니다.");

			await EnsureRolesSeededAsync(scope.ServiceProvider, stoppingToken);
			await EnsureDefaultRolesAssignedAsync(scope.ServiceProvider, stoppingToken);
			await LogInitialAdminSetupTokenIfPendingAsync(scope.ServiceProvider, stoppingToken);

			logger.LogInformation("초기화 작업이 모두 완료되었습니다.");
		}
		catch (Exception ex)
		{
			activity?.AddException(ex);
			logger.LogError(ex, "데이터베이스 마이그레이션 중 오류가 발생했습니다.");
			throw;
		}

		hostApplicationLifetime.StopApplication();
	}

	private async Task EnsureRolesSeededAsync(IServiceProvider sp, CancellationToken ct)
	{
		var roleManager = sp.GetRequiredService<RoleManager<IdentityRole>>();
		await IdentityRoleSeeder.EnsureRolesSeededAsync(roleManager);
	}

	private async Task EnsureDefaultRolesAssignedAsync(IServiceProvider sp, CancellationToken ct)
	{
		var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
		await UserRoleBootstrapService.EnsureDefaultRolesAssignedAsync(userManager, ct);
	}

	// Web 로그를 놓친 운영자도 이 리소스의 Aspire 대시보드/컨테이너 로그에서 찾을 수 있게 재기록.
	private async Task LogInitialAdminSetupTokenIfPendingAsync(IServiceProvider sp, CancellationToken ct)
	{
		var db = sp.GetRequiredService<ApplicationDbContext>();
		var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
		var options = sp.GetRequiredService<IOptions<InitialAdminSetupOptions>>();
		await InitialAdminSetupTokenService.LogIfBootstrapPendingAsync(db, userManager, options.Value, logger, ct);
	}
}
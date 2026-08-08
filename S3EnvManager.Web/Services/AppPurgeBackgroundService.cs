using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class AppPurgeBackgroundService(IServiceScopeFactory scopeFactory) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using (var scope = scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
				var store = scope.ServiceProvider.GetRequiredService<ISecretObjectStore>();
				var primaryStorageSettingsStore =
					scope.ServiceProvider.GetRequiredService<IPrimaryStorageSettingsStore>();
				// 같은 스코프에서 꺼내야 감사 로그가 퍼지 트랜잭션에 함께 커밋된다.
				var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
				await AppPurgeService.PurgeEligibleAppsAsync(
					db, store, primaryStorageSettingsStore, auditLogger, TimeProvider.System, stoppingToken)
					.ConfigureAwait(false);
			}

			try
			{
				await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}
}
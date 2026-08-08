using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class AppPurgeBackgroundService(
	IServiceScopeFactory scopeFactory, ILogger<AppPurgeBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromHours(1);

	protected override Task ExecuteCycleAsync(IServiceProvider services, CancellationToken cancellationToken)
	{
		var db = services.GetRequiredService<ApplicationDbContext>();
		var store = services.GetRequiredService<ISecretObjectStore>();
		var primaryStorageSettingsStore = services.GetRequiredService<IPrimaryStorageSettingsStore>();
		// 같은 스코프에서 꺼내야 감사 로그가 퍼지 트랜잭션에 함께 커밋된다.
		var auditLogger = services.GetRequiredService<IAuditLogger>();
		return AppPurgeService.PurgeEligibleAppsAsync(
			db, store, primaryStorageSettingsStore, auditLogger, TimeProvider.System, cancellationToken);
	}
}

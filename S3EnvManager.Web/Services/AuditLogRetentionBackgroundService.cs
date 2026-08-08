using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class AuditLogRetentionBackgroundService(
	IServiceScopeFactory scopeFactory, ILogger<AuditLogRetentionBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromHours(6);

	protected override Task ExecuteCycleAsync(IServiceProvider services, CancellationToken cancellationToken) =>
		AuditLogRetentionService.DeleteExpiredLogsAsync(
			services.GetRequiredService<ApplicationDbContext>(), TimeProvider.System, cancellationToken);
}

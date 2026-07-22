using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class AuditLogRetentionBackgroundService(IServiceScopeFactory scopeFactory) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using (var scope = scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
				await AuditLogRetentionService.DeleteExpiredLogsAsync(db, TimeProvider.System, stoppingToken)
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
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
				await AppPurgeService.PurgeEligibleAppsAsync(
					db, store, primaryStorageSettingsStore, TimeProvider.System, stoppingToken)
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
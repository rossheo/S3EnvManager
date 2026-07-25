using Microsoft.AspNetCore.DataProtection;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

/// <summary>24시간마다 D-Day를 설정한 사용자에게 만료 임박 키를 Discord로 알린다.</summary>
public sealed class KeyExpirationNotificationBackgroundService(IServiceScopeFactory scopeFactory)
	: BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using (var scope = scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
				var notifier = scope.ServiceProvider.GetRequiredService<IDiscordNotifier>();
				var dataProtectionProvider =
					scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
				await KeyExpirationNotificationService.CheckAndNotifyAsync(
					db, notifier, dataProtectionProvider, TimeProvider.System, stoppingToken)
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

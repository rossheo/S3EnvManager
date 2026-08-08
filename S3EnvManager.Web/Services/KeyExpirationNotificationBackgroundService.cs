using Microsoft.AspNetCore.DataProtection;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

/// <summary>24시간마다 D-Day를 설정한 사용자에게 만료 임박 키를 Discord로 알린다.</summary>
public sealed class KeyExpirationNotificationBackgroundService(
	IServiceScopeFactory scopeFactory, ILogger<KeyExpirationNotificationBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromHours(24);

	protected override Task ExecuteCycleAsync(
		IServiceProvider services, CancellationToken cancellationToken) =>
		KeyExpirationNotificationService.CheckAndNotifyAsync(
			services.GetRequiredService<ApplicationDbContext>(),
			services.GetRequiredService<IDiscordNotifier>(),
			services.GetRequiredService<IDataProtectionProvider>(),
			TimeProvider.System, cancellationToken);
}

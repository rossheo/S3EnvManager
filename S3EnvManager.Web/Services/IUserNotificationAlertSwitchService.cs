namespace S3EnvManager.Web.Services;

public sealed record UserNotificationAlertSwitchInfo(string AlertType, bool Enabled, string Description);

public interface IUserNotificationAlertSwitchService
{
	Task<bool> IsEnabledAsync(string userId, string alertType, CancellationToken cancellationToken = default);

	Task<List<UserNotificationAlertSwitchInfo>> ListAsync(
		string userId, CancellationToken cancellationToken = default);

	Task SetEnabledAsync(
		string userId, string alertType, bool enabled, CancellationToken cancellationToken = default);
}

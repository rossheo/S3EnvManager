namespace S3EnvManager.Web.Services;

public sealed record UserNotificationSettingsInfo(string? WebhookUrl, Int32? NotifyDaysBeforeExpiration);

public interface IUserNotificationSettingsService
{
	public const Int32 MinNotifyDays = 1;
	public const Int32 MaxNotifyDays = 3650;

	// DataProtection IDataProtectionProvider.CreateProtector에 넘기는 고정 purpose 문자열.
	public const string ProtectorPurpose = "S3EnvManager.UserNotificationSettings.DiscordWebhook";

	Task<UserNotificationSettingsInfo> GetAsync(string userId, CancellationToken cancellationToken = default);

	// webhookUrl/dDayDays가 둘 다 비어있으면 알림을 비활성화한다(기존 설정 삭제). 검증 실패 시
	// null이 아닌 오류 메시지를 반환한다(예외를 던지지 않음 - 폼 오류 표시용).
	Task<string?> SaveAsync(
		string userId, string? webhookUrl, Int32? dDayDays, CancellationToken cancellationToken = default);
}

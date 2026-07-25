namespace S3EnvManager.Database.Models;

/// <summary>사용자 1명당 1행(싱글턴이 아니라 UserId가 곧 PK). 웹훅을 등록하지 않았거나 D-Day를
/// 설정하지 않은 사용자는 이 테이블에 행이 아예 없을 수 있다.</summary>
public class UserNotificationSettings
{
	public required string UserId { get; init; }

	public ApplicationUser? User { get; init; }

	/// <summary>ASP.NET Core DataProtection으로 암호화된 문자열(KMS envelope 아님) - 저장 시
	/// Protect(), 읽을 때 Unprotect().</summary>
	public string? ProtectedDiscordWebhookUrl { get; set; }

	/// <summary>만료 며칠 전부터 알림을 받을지(예: 7, 100). null이면 키 만료 알림 비활성.</summary>
	public Int32? NotifyDaysBeforeExpiration { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

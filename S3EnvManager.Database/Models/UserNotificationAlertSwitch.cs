namespace S3EnvManager.Database.Models;

/// <summary>사용자가 특정 알림 종류를 껐을 때만 행이 남는다(FeatureSwitch와 동일한 관례) -
/// 행이 없으면 <see cref="UserNotificationAlertTypes.Known"/>의 기본값을 쓴다.</summary>
public class UserNotificationAlertSwitch
{
	public required string UserId { get; init; }

	public ApplicationUser? User { get; init; }

	public required string AlertType { get; init; }

	public bool Enabled { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

public static class UserNotificationAlertTypes
{
	public const string KeyExpiration = "KeyExpiration";

	public static readonly IReadOnlyList<(string Key, bool DefaultEnabled, string Description)> Known =
	[
		(KeyExpiration, true, "설정한 D-Day 안에 든(또는 이미 지난) 키 만료 알림"),
	];
}

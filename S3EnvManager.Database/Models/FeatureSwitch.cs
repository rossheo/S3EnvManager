namespace S3EnvManager.Database.Models;

/// <summary>이 테이블에는 관리자가 기본값을 실제로 바꾼 키만 행으로 남는다(바꾼 적 없는
/// 키는 <see cref="FeatureSwitchKeys"/>의 기본값을 그대로 쓴다).</summary>
public class FeatureSwitch
{
	public required string Key { get; init; }

	public bool Enabled { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

public static class FeatureSwitchKeys
{
	public const string AllowRegistration = "AllowRegistration";
	public const string AllowForgotPassword = "AllowForgotPassword";
	public const string AllowResendEmailConfirmation = "AllowResendEmailConfirmation";
	public const string AutoProvisioningSelfHeal = "AutoProvisioningSelfHeal";
	public const string ReuseDataKeyOnSave = "ReuseDataKeyOnSave";

	public static readonly IReadOnlyList<(string Key, bool DefaultEnabled, string Description)> Known =
	[
		(AllowRegistration, true, "꺼져 있으면 신규 사용자 회원가입을 막는다(기존 사용자 로그인은 영향 없음)."),
		(AllowForgotPassword, false, "꺼져 있으면 비밀번호 재설정 요청(Account/ForgotPassword)을 막는다."),
		(AllowResendEmailConfirmation, false, "꺼져 있으면 이메일 확인 재발송 요청(Account/ResendEmailConfirmation)을 막는다."),
		(AutoProvisioningSelfHeal, false,
			"켜져 있고 admin 부트스트랩 자격증명이 등록돼 있으면, 매 기동마다 부트스트랩 app IAM 사용자/CMK 2개/키 정책/CMK " +
			"레지스트리/app 정책·Access Key를 자동으로 재확인·복구한다(S3 버킷은 대상이 아님 - 버킷 자가 치유가 이미 담당). " +
			"실패해도 기동을 막지 않는다."),
		(ReuseDataKeyOnSave, false,
			"켜면 번들 저장 시 감싼 데이터 키를 같은 App 안에서 최대 10분/50회까지 재사용해 KMS 호출을 " +
			"저장당 2회에서 0회로 줄인다(KMS free tier 절감용). 대가는 데이터 키 하나가 그 창 안의 여러 번들을 " +
			"함께 보호하게 되는 것이다 - 그 키가 유출되면 한 번들이 아니라 그 전부가 노출된다. " +
			"재사용 범위는 App과 CMK ARN 조합으로 한정되며 CMK를 승격하면 자동으로 새 키를 쓴다."),
	];
}
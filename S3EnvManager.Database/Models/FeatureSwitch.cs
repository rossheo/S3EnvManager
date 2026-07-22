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

	public static readonly IReadOnlyList<(string Key, bool DefaultEnabled, string Description)> Known =
	[
		(AllowRegistration, true, "꺼져 있으면 신규 사용자 회원가입을 막는다(기존 사용자 로그인은 영향 없음)."),
		(AllowForgotPassword, false, "꺼져 있으면 비밀번호 재설정 요청(Account/ForgotPassword)을 막는다."),
		(AllowResendEmailConfirmation, false, "꺼져 있으면 이메일 확인 재발송 요청(Account/ResendEmailConfirmation)을 막는다."),
		(AutoProvisioningSelfHeal, false,
			"켜져 있고 admin 부트스트랩 자격증명이 등록돼 있으면, 매 기동마다 부트스트랩 app IAM 사용자/CMK 2개/키 정책/CMK " +
			"레지스트리/app 정책·Access Key를 자동으로 재확인·복구한다(S3 버킷은 대상이 아님 - 버킷 자가 치유가 이미 담당). " +
			"실패해도 기동을 막지 않는다."),
	];
}
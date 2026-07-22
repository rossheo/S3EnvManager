using System.Text.RegularExpressions;

namespace S3EnvManager.Web.Services;

// App 이름은 IAM 사용자 이름(s3envmanager-app-{name})으로도 쓰이므로, IAM UserName 규칙(영숫자 +
// +=,.@_- 만, 최대 64자)으로 등록 시점에 미리 검증해 자격증명 발급 시점의 실패를 앞당긴다.
public static partial class AppNameValidator
{
	private const Int32 IamUserNameMaxLength = 64;
	// 17자 - IamAppCredentialProvisioner.UserName과 일치해야 한다.
	public const Int32 MaxLength = IamUserNameMaxLength - 17;

	[GeneratedRegex("^[A-Za-z0-9+=,.@_-]+$")]
	private static partial Regex AllowedCharsPattern();

	public static string? Validate(string? name)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return "App 이름을 입력하세요.";
		}
		if (name.Length > MaxLength)
		{
			return $"App 이름은 최대 {MaxLength}자까지 가능합니다(IAM 사용자 이름 길이 제한).";
		}
		if (!AllowedCharsPattern().IsMatch(name))
		{
			return "App 이름은 영문자, 숫자, 그리고 + = , . @ _ - 만 사용할 수 있습니다(IAM 사용자 이름 규칙).";
		}
		return null;
	}
}
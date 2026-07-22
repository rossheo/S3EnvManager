using System.Text.RegularExpressions;

namespace S3EnvManager.Web.Services;

/// <summary>형식 검증만 수행한다 - 실제 권한/키 존재 여부는 최종적으로 KMS가 판단한다.</summary>
public static partial class CmkArnValidator
{
	[GeneratedRegex(@"^arn:aws[a-z0-9-]*:kms:[a-z0-9-]+:\d{12}:key/[a-zA-Z0-9-]+$")]
	private static partial Regex AllowedPattern();

	/// <summary>유효하면 null, 아니면 사용자에게 보여줄 오류 메시지.</summary>
	public static string? Validate(string? arn)
	{
		if (string.IsNullOrWhiteSpace(arn))
		{
			return "CMK ARN을 입력하세요.";
		}
		if (!AllowedPattern().IsMatch(arn))
		{
			return "CMK ARN 형식이 올바르지 않습니다(예: " +
				"arn:aws:kms:ap-northeast-2:123456789012:key/xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx).";
		}
		return null;
	}
}
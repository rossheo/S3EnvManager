using System.Text.RegularExpressions;

namespace S3EnvManager.Web.Services;

/// <summary>버킷 이름은 <see cref="IamAppCredentialProvisioner.BuildPolicyDocument"/>가 문자열 보간으로
/// 그대로 IAM 정책 JSON에 삽입하므로, 여기서 S3 네이밍 규칙만 통과해도 안전한 문자만 허용된다.</summary>
public static partial class BucketNameValidator
{
	private const Int32 MinLength = 3;
	private const Int32 MaxLength = 63;

	[GeneratedRegex("^[a-z0-9][a-z0-9.-]*[a-z0-9]$")]
	private static partial Regex AllowedPattern();

	[GeneratedRegex(@"\.\.")]
	private static partial Regex ConsecutivePeriodsPattern();

	[GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$")]
	private static partial Regex IpAddressPattern();

	/// <summary>유효하면 null, 아니면 사용자에게 보여줄 오류 메시지.</summary>
	public static string? Validate(string? bucket)
	{
		if (string.IsNullOrWhiteSpace(bucket))
		{
			return "버킷 이름을 입력하세요.";
		}
		if (bucket.Length is < MinLength or > MaxLength)
		{
			return $"버킷 이름은 {MinLength}~{MaxLength}자여야 합니다(S3 버킷 네이밍 규칙).";
		}
		if (!AllowedPattern().IsMatch(bucket))
		{
			return "버킷 이름은 소문자, 숫자, 마침표(.), 하이픈(-)만 사용할 수 있고 소문자/숫자로 시작·끝나야 합니다(S3 버킷 네이밍 규칙).";
		}
		if (ConsecutivePeriodsPattern().IsMatch(bucket))
		{
			return "버킷 이름에 마침표(.)를 연속으로 쓸 수 없습니다(S3 버킷 네이밍 규칙).";
		}
		if (IpAddressPattern().IsMatch(bucket))
		{
			return "버킷 이름을 IP 주소 형식으로 쓸 수 없습니다(S3 버킷 네이밍 규칙).";
		}
		return null;
	}
}
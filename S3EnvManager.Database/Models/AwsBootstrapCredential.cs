using S3EnvManager.Sops;

namespace S3EnvManager.Database.Models;

/// <summary>KMS로 암호화하면 "이 자격증명을 풀 열쇠가 이 자격증명 자체"인 순환 참조가
/// 생기므로, ASP.NET Core DataProtection(AWS 호출 불필요한 로컬 대칭키)으로 암호화해 보관한다.</summary>
public class AwsBootstrapCredential
{
	public required CmkRole Role { get; init; }

	public required string ProtectedAccessKeyId { get; set; }

	public required string ProtectedSecretAccessKey { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}
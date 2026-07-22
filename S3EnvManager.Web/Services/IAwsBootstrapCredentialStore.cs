using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// DataProtection(로컬 대칭키)으로 암호화한다 - KMS로 감싸면 순환 참조가 생기기 때문.
public interface IAwsBootstrapCredentialStore
{
	Task SaveAsync(
		CmkRole role, string accessKeyId, string secretAccessKey, CancellationToken cancellationToken = default);

	Task<(string AccessKeyId, string SecretAccessKey)?> GetAsync(
		CmkRole role, CancellationToken cancellationToken = default);

	Task ClearAsync(CmkRole role, CancellationToken cancellationToken = default);
}
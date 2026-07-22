namespace S3EnvManager.Web.Services;

public sealed record ProvisionedCredential(string AccessKeyId, string SecretAccessKey);

// 발급된 자격증명은 AWS IAM만 인식하므로 주 저장소는 항상 AWS S3여야 한다 - MinIO 등
// S3 호환 스토리지는 이 자격증명을 인식하지 못한다.
public interface IAppCredentialProvisioner
{
	// appFacingCmkArns는 활성+보조 CMK 전부여야 한다 - 활성 CMK만 부여하면 승격 후
	// 옛 CMK로 감싸진 시크릿을 못 읽게 된다.
	Task<ProvisionedCredential> IssueAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default);

	Task ReapplyPolicyAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default);

	Task RevokeAccessKeyAsync(string appName, string accessKeyId, CancellationToken cancellationToken = default);

	Task DeleteUserAsync(string appName, CancellationToken cancellationToken = default);
}
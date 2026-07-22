namespace S3EnvManager.Web.Services;

// 부트스트랩 app identity("s3envmanager-app" 단일 IAM 사용자) 전용 프로비저너 - 앱별
// 사용자(IamAppCredentialProvisioner)와 대상/정책이 달라 별도 클래스로 둔다.
public interface IBootstrapAppIdentityProvisioner
{
	Task<string> EnsureUserAsync(CancellationToken cancellationToken = default);

	Task PutPolicyAsync(IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default);

	// 부트스트랩 IAM 사용자가 아직 없으면 예외 없이 false를 반환하는 best-effort 버전.
	Task<bool> TryPutPolicyIfProvisionedAsync(
		IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default);

	Task<ProvisionedCredential> IssueAccessKeyAsync(CancellationToken cancellationToken = default);

	Task<IReadOnlyList<string>> ListAccessKeyIdsAsync(CancellationToken cancellationToken = default);

	Task DeleteAccessKeyAsync(string accessKeyId, CancellationToken cancellationToken = default);
}
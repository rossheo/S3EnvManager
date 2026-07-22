namespace S3EnvManager.Web.Services;

// CMK 자체의 수명주기(생성/태깅/별칭/로테이션/키 정책) 관리 전용 - IKmsKeyOperations(봉투암호화)와는 별개.
public interface IKmsKeyAdministration
{
	Task<string?> FindKeyArnByAliasAsync(string alias, CancellationToken cancellationToken = default);

	// tags에 "s3envmanager-managed"=true를 반드시 포함해야 admin 정책의 태그 조건 스코프에 들어온다.
	Task<string> CreateKeyAsync(
		string description, IReadOnlyDictionary<string, string> tags,
		CancellationToken cancellationToken = default);

	Task EnsureAliasAsync(string alias, string keyArn, CancellationToken cancellationToken = default);

	Task EnableRotationAsync(string keyArn, CancellationToken cancellationToken = default);

	Task PutKeyPolicyAsync(string keyArn, string policyJson, CancellationToken cancellationToken = default);

	Task TagKeyAsync(
		string keyArn, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default);
}
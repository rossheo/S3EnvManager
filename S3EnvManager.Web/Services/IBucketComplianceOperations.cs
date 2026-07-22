namespace S3EnvManager.Web.Services;

public sealed record PublicAccessBlockState(
	bool BlockPublicAcls, bool IgnorePublicAcls, bool BlockPublicPolicy, bool RestrictPublicBuckets);

// MinIO 등 일부 S3 호환 스토리지가 구현하지 않는 오퍼레이션을 호출했을 때 던진다.
// BucketSelfHealService는 이를 잡아 해당 컨트롤만 건너뛰고 버킷 등록은 막지 않는다.
public sealed class BucketOperationNotSupportedException(string message) : Exception(message);

public interface IBucketComplianceOperations
{
	Task<bool> IsVersioningEnabledAsync(string bucketName, CancellationToken cancellationToken = default);

	Task EnableVersioningAsync(string bucketName, CancellationToken cancellationToken = default);

	Task<PublicAccessBlockState?> GetPublicAccessBlockAsync(
		string bucketName, CancellationToken cancellationToken = default);

	Task PutPublicAccessBlockAsync(
		string bucketName, PublicAccessBlockState state, CancellationToken cancellationToken = default);

	Task<bool> IsObjectOwnershipEnforcedAsync(string bucketName, CancellationToken cancellationToken = default);

	Task EnforceObjectOwnershipAsync(string bucketName, CancellationToken cancellationToken = default);

	Task<bool> HasLifecycleRuleAsync(string bucketName, CancellationToken cancellationToken = default);

	Task AddNoncurrentVersionExpirationRuleAsync(
		string bucketName, string ruleId, Int32 noncurrentDays, CancellationToken cancellationToken = default);

	Task<string?> GetBucketPolicyAsync(string bucketName, CancellationToken cancellationToken = default);
}

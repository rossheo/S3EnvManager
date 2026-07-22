namespace S3EnvManager.Web.Services;

// TlsEnforced는 감지만 하고 자동 수정하지 않는 보류 항목이라 별도로 구분한다.
public sealed record BucketHealthReport(
	string Bucket,
	bool VersioningEnabled,
	bool PublicAccessBlocked,
	bool ObjectOwnershipEnforced,
	bool LifecycleRuleConfigured,
	bool TlsEnforced,
	DateTimeOffset CheckedAt);

public interface IBucketSelfHealService
{
	// Public Access Block/Object Ownership은 일부 S3 호환 스토리지가 API를 구현하지 않을 수
	// 있어, NotImplemented만 예외 없이 false로 보고하고 계속 진행한다.
	Task<BucketHealthReport> HealAsync(string bucketName, CancellationToken cancellationToken = default);
}
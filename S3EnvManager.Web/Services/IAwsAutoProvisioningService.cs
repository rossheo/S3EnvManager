namespace S3EnvManager.Web.Services;

public enum ProvisioningStepStatus
{
	Done,
	AlreadyProvisioned,
	Failed,
	SkippedDueToEarlierFailure,
}

public sealed record ProvisioningStep(string Name, ProvisioningStepStatus Status, string Detail);

public sealed record ProvisioningReport(IReadOnlyList<ProvisioningStep> Steps)
{
	public bool Succeeded => Steps.Count > 0 && Steps.All(s => s.Status != ProvisioningStepStatus.Failed);
}

public sealed record ProvisioningRequest(string Bucket, string Region, bool CreateBucketIfMissing);

// 모든 단계가 ensure-멱등이라 EnsureProvisionedAsync를 몇 번을 재실행해도 안전하다 - 실패한
// 지점부터 재개하는 효과를 낸다(별도의 롤백/재개 로직이 없다).
public interface IAwsAutoProvisioningService
{
	// includeBucketProvisioning=false면 S3 버킷 확인/생성과 주 저장소 설정 저장 단계를 건너뛴다 -
	// BucketSelfHealBackgroundService가 이미 독립적으로 치유하므로 매 기동마다 재확인할 필요가 없다.
	Task<ProvisioningReport> EnsureProvisionedAsync(
		ProvisioningRequest request, string? actorUserId = null, bool includeBucketProvisioning = true,
		CancellationToken cancellationToken = default);
}
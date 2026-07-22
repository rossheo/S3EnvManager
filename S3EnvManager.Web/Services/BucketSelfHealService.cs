namespace S3EnvManager.Web.Services;

public sealed class BucketSelfHealService(
	IBucketComplianceOperations bucket, IAuditLogger auditLogger) : IBucketSelfHealService
{
	private const Int32 NoncurrentVersionExpirationDays = 180;
	private const string LifecycleRuleId = "s3envmanager-noncurrent-version-expiration";

	public async Task<BucketHealthReport> HealAsync(
		string bucketName, CancellationToken cancellationToken = default)
	{
		var changesApplied = new List<string>();

		var versioningEnabled = await EnsureVersioningEnabledAsync(bucketName, changesApplied, cancellationToken)
			.ConfigureAwait(false);
		var publicAccessBlocked = await EnsurePublicAccessBlockedAsync(
			bucketName, changesApplied, cancellationToken).ConfigureAwait(false);
		var objectOwnershipEnforced = await EnsureObjectOwnershipEnforcedAsync(
			bucketName, changesApplied, cancellationToken).ConfigureAwait(false);
		var lifecycleConfigured = await EnsureLifecycleRuleAsync(bucketName, changesApplied, cancellationToken)
			.ConfigureAwait(false);
		var tlsEnforced = await DetectTlsEnforcementAsync(bucketName, cancellationToken).ConfigureAwait(false);

		// 이미 정상이었던 확인만으로는 로그를 남기지 않는다.
		if (changesApplied.Count > 0)
		{
			var details = System.Text.Json.JsonSerializer.Serialize(
				new { bucket = bucketName, changes = changesApplied }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.BucketSelfHealed, actorUserId: null, appId: null, details, cancellationToken)
				.ConfigureAwait(false);
		}

		return new BucketHealthReport(
			bucketName, versioningEnabled, publicAccessBlocked, objectOwnershipEnforced, lifecycleConfigured,
			tlsEnforced, DateTimeOffset.UtcNow);
	}

	private async Task<bool> EnsureVersioningEnabledAsync(
		string bucketName, List<string> changesApplied, CancellationToken cancellationToken)
	{
		if (await bucket.IsVersioningEnabledAsync(bucketName, cancellationToken).ConfigureAwait(false))
		{
			return true;
		}

		await bucket.EnableVersioningAsync(bucketName, cancellationToken).ConfigureAwait(false);
		changesApplied.Add("버저닝 활성화");
		return true;
	}

	// MinIO 등 API 미구현 스토리지에서는 이 컨트롤만 false로 건너뛰어 대시보드에 남긴다(등록은 막지 않음).
	private async Task<bool> EnsurePublicAccessBlockedAsync(
		string bucketName, List<string> changesApplied, CancellationToken cancellationToken)
	{
		var desired = new PublicAccessBlockState(
			BlockPublicAcls: true, IgnorePublicAcls: true, BlockPublicPolicy: true, RestrictPublicBuckets: true);

		PublicAccessBlockState? current;
		try
		{
			current = await bucket.GetPublicAccessBlockAsync(bucketName, cancellationToken).ConfigureAwait(false);
		}
		catch (BucketOperationNotSupportedException)
		{
			return false;
		}

		if (current == desired)
		{
			return true;
		}

		try
		{
			await bucket.PutPublicAccessBlockAsync(bucketName, desired, cancellationToken).ConfigureAwait(false);
		}
		catch (BucketOperationNotSupportedException)
		{
			return false;
		}
		changesApplied.Add("Public Access Block 설정");
		return true;
	}

	private async Task<bool> EnsureObjectOwnershipEnforcedAsync(
		string bucketName, List<string> changesApplied, CancellationToken cancellationToken)
	{
		bool alreadyEnforced;
		try
		{
			alreadyEnforced = await bucket.IsObjectOwnershipEnforcedAsync(bucketName, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (BucketOperationNotSupportedException)
		{
			return false;
		}

		if (alreadyEnforced)
		{
			return true;
		}

		try
		{
			await bucket.EnforceObjectOwnershipAsync(bucketName, cancellationToken).ConfigureAwait(false);
		}
		catch (BucketOperationNotSupportedException)
		{
			return false;
		}
		changesApplied.Add("Object Ownership(BucketOwnerEnforced) 설정");
		return true;
	}

	// 이미 어떤 lifecycle 규칙이든 설정돼 있으면 운영자가 의도적으로 바꾼 값을 존중해 건드리지 않는다.
	private async Task<bool> EnsureLifecycleRuleAsync(
		string bucketName, List<string> changesApplied, CancellationToken cancellationToken)
	{
		if (await bucket.HasLifecycleRuleAsync(bucketName, cancellationToken).ConfigureAwait(false))
		{
			return true;
		}

		await bucket.AddNoncurrentVersionExpirationRuleAsync(
			bucketName, LifecycleRuleId, NoncurrentVersionExpirationDays, cancellationToken).ConfigureAwait(false);
		changesApplied.Add($"Noncurrent 버전 만료 lifecycle 규칙 추가({NoncurrentVersionExpirationDays}일)");
		return true;
	}

	// 버킷 정책은 단일 JSON 문서라 통째로 덮어쓰면 다른 statement를 지울 위험이 있어 감지만 한다.
	private async Task<bool> DetectTlsEnforcementAsync(string bucketName, CancellationToken cancellationToken)
	{
		var policy = await bucket.GetBucketPolicyAsync(bucketName, cancellationToken).ConfigureAwait(false);
		return policy is not null && policy.Contains("aws:SecureTransport", StringComparison.Ordinal);
	}
}

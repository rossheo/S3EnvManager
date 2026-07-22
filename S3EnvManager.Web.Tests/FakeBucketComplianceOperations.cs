using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>실 S3 없이 버킷 상태를 인메모리로 흉내낸다. Put* 호출 횟수로 멱등성을 검증한다.</summary>
public sealed class FakeBucketComplianceOperations : IBucketComplianceOperations
{
	private sealed class BucketState
	{
		public bool VersioningEnabled;
		public PublicAccessBlockState? PublicAccessBlock;
		public bool ObjectOwnershipEnforced;
		public bool HasLifecycleRule;
		public string? Policy;
	}

	private readonly Dictionary<string, BucketState> buckets = [];

	public Int32 EnableVersioningCallCount { get; private set; }
	public Int32 PutPublicAccessBlockCallCount { get; private set; }
	public Int32 EnforceObjectOwnershipCallCount { get; private set; }
	public Int32 AddLifecycleRuleCallCount { get; private set; }

	private BucketState State(string bucketName)
	{
		if (!buckets.TryGetValue(bucketName, out var state))
		{
			state = new BucketState();
			buckets[bucketName] = state;
		}
		return state;
	}

	public void SeedExistingLifecycleRule(string bucketName) => State(bucketName).HasLifecycleRule = true;

	public void SeedBucketPolicy(string bucketName, string policy) => State(bucketName).Policy = policy;

	public Task<bool> IsVersioningEnabledAsync(string bucketName, CancellationToken cancellationToken = default) =>
		Task.FromResult(State(bucketName).VersioningEnabled);

	public Task EnableVersioningAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		EnableVersioningCallCount++;
		State(bucketName).VersioningEnabled = true;
		return Task.CompletedTask;
	}

	public Task<PublicAccessBlockState?> GetPublicAccessBlockAsync(
		string bucketName, CancellationToken cancellationToken = default) =>
		Task.FromResult(State(bucketName).PublicAccessBlock);

	public Task PutPublicAccessBlockAsync(
		string bucketName, PublicAccessBlockState state, CancellationToken cancellationToken = default)
	{
		PutPublicAccessBlockCallCount++;
		State(bucketName).PublicAccessBlock = state;
		return Task.CompletedTask;
	}

	public Task<bool> IsObjectOwnershipEnforcedAsync(
		string bucketName, CancellationToken cancellationToken = default) =>
		Task.FromResult(State(bucketName).ObjectOwnershipEnforced);

	public Task EnforceObjectOwnershipAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		EnforceObjectOwnershipCallCount++;
		State(bucketName).ObjectOwnershipEnforced = true;
		return Task.CompletedTask;
	}

	public Task<bool> HasLifecycleRuleAsync(string bucketName, CancellationToken cancellationToken = default) =>
		Task.FromResult(State(bucketName).HasLifecycleRule);

	public Task AddNoncurrentVersionExpirationRuleAsync(
		string bucketName, string ruleId, Int32 noncurrentDays, CancellationToken cancellationToken = default)
	{
		AddLifecycleRuleCallCount++;
		State(bucketName).HasLifecycleRule = true;
		return Task.CompletedTask;
	}

	public Task<string?> GetBucketPolicyAsync(string bucketName, CancellationToken cancellationToken = default) =>
		Task.FromResult(State(bucketName).Policy);
}

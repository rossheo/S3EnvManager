using Amazon.S3;
using Amazon.S3.Model;

namespace S3EnvManager.Web.Services;

public sealed class S3BucketComplianceOperations(IAmazonS3ClientProvider s3ClientProvider)
	: IBucketComplianceOperations
{
	private IAmazonS3 s3Client => s3ClientProvider.GetClient();

	public async Task<bool> IsVersioningEnabledAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		var current = await s3Client.GetBucketVersioningAsync(bucketName, cancellationToken).ConfigureAwait(false);
		return current.VersioningConfig?.Status == VersionStatus.Enabled;
	}

	public Task EnableVersioningAsync(string bucketName, CancellationToken cancellationToken = default) =>
		s3Client.PutBucketVersioningAsync(new PutBucketVersioningRequest
		{
			BucketName = bucketName,
			VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
		}, cancellationToken);

	public async Task<PublicAccessBlockState?> GetPublicAccessBlockAsync(
		string bucketName, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await s3Client.GetPublicAccessBlockAsync(
				new GetPublicAccessBlockRequest { BucketName = bucketName }, cancellationToken)
				.ConfigureAwait(false);
			var config = response.PublicAccessBlockConfiguration;
			return config is null
				? null
				: new PublicAccessBlockState(
					config.BlockPublicAcls ?? false, config.IgnorePublicAcls ?? false,
					config.BlockPublicPolicy ?? false, config.RestrictPublicBuckets ?? false);
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchPublicAccessBlockConfiguration")
		{
			return null;
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode == "NotImplemented")
		{
			throw new BucketOperationNotSupportedException("이 스토리지는 Public Access Block GET을 지원하지 않습니다.");
		}
	}

	public async Task PutPublicAccessBlockAsync(
		string bucketName, PublicAccessBlockState state, CancellationToken cancellationToken = default)
	{
		try
		{
			await s3Client.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
			{
				BucketName = bucketName,
				PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
				{
					BlockPublicAcls = state.BlockPublicAcls,
					IgnorePublicAcls = state.IgnorePublicAcls,
					BlockPublicPolicy = state.BlockPublicPolicy,
					RestrictPublicBuckets = state.RestrictPublicBuckets,
				},
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode is "NotImplemented" or "MalformedXML")
		{
			throw new BucketOperationNotSupportedException("이 스토리지는 Public Access Block PUT을 지원하지 않습니다.");
		}
	}

	public async Task<bool> IsObjectOwnershipEnforcedAsync(
		string bucketName, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await s3Client.GetBucketOwnershipControlsAsync(
				new GetBucketOwnershipControlsRequest { BucketName = bucketName }, cancellationToken)
				.ConfigureAwait(false);
			return response.OwnershipControls?.Rules?
				.Any(r => r.ObjectOwnership == ObjectOwnership.BucketOwnerEnforced) == true;
		}
		catch (AmazonS3Exception ex)
			when (ex.ErrorCode is "OwnershipControlsNotFoundError" or "NoSuchOwnershipControls")
		{
			return false;
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode == "NotImplemented")
		{
			throw new BucketOperationNotSupportedException("이 스토리지는 Object Ownership Controls GET을 지원하지 않습니다.");
		}
	}

	public async Task EnforceObjectOwnershipAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		try
		{
			await s3Client.PutBucketOwnershipControlsAsync(new PutBucketOwnershipControlsRequest
			{
				BucketName = bucketName,
				OwnershipControls = new OwnershipControls
				{
					Rules = [new OwnershipControlsRule { ObjectOwnership = ObjectOwnership.BucketOwnerEnforced }],
				},
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode is "NotImplemented" or "MalformedXML")
		{
			throw new BucketOperationNotSupportedException("이 스토리지는 Object Ownership Controls PUT을 지원하지 않습니다.");
		}
	}

	public async Task<bool> HasLifecycleRuleAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await s3Client.GetLifecycleConfigurationAsync(bucketName, cancellationToken)
				.ConfigureAwait(false);
			return (response.Configuration?.Rules ?? []).Count > 0;
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchLifecycleConfiguration")
		{
			return false;
		}
	}

	public Task AddNoncurrentVersionExpirationRuleAsync(
		string bucketName, string ruleId, Int32 noncurrentDays, CancellationToken cancellationToken = default) =>
		s3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
		{
			BucketName = bucketName,
			Configuration = new LifecycleConfiguration
			{
				Rules =
				[
					new LifecycleRule
					{
						Id = ruleId,
						Status = LifecycleRuleStatus.Enabled,
						Filter = new LifecycleFilter
						{
							LifecycleFilterPredicate = new LifecyclePrefixPredicate { Prefix = string.Empty },
						},
						NoncurrentVersionExpiration = new LifecycleRuleNoncurrentVersionExpiration
						{
							NoncurrentDays = noncurrentDays,
						},
					},
				],
			},
		}, cancellationToken);

	public async Task<string?> GetBucketPolicyAsync(string bucketName, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await s3Client.GetBucketPolicyAsync(bucketName, cancellationToken).ConfigureAwait(false);
			return response.Policy;
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode is "NoSuchBucketPolicy")
		{
			return null;
		}
	}
}

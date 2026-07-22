using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace S3EnvManager.Web.Services;

public sealed class AwsKmsKeyAdministration(IAmazonKeyManagementService client) : IKmsKeyAdministration
{
	public async Task<string?> FindKeyArnByAliasAsync(
		string alias, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await client.DescribeKeyAsync(new DescribeKeyRequest { KeyId = alias }, cancellationToken)
				.ConfigureAwait(false);
			return response.KeyMetadata.Arn;
		}
		catch (NotFoundException)
		{
			return null;
		}
	}

	public async Task<string> CreateKeyAsync(
		string description, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
	{
		var response = await client.CreateKeyAsync(new CreateKeyRequest
		{
			Description = description,
			KeySpec = KeySpec.SYMMETRIC_DEFAULT,
			KeyUsage = KeyUsageType.ENCRYPT_DECRYPT,
			Tags = tags.Select(kv => new Tag { TagKey = kv.Key, TagValue = kv.Value }).ToList(),
		}, cancellationToken).ConfigureAwait(false);

		return response.KeyMetadata.Arn;
	}

	public async Task EnsureAliasAsync(
		string alias, string keyArn, CancellationToken cancellationToken = default)
	{
		try
		{
			await client.CreateAliasAsync(
				new CreateAliasRequest { AliasName = alias, TargetKeyId = keyArn }, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (AlreadyExistsException)
		{
			// 재실행(ensure-멱등) - 이미 이 키를 가리키는 별칭이 있으면 그대로 둔다. 다른 키를
			// 가리키고 있다면(운영자가 콘솔에서 바꿨거나 하는 드문 경우) UpdateAlias까지는
			// 자동으로 하지 않는다 - 어떤 별칭이 "진짜"인지 판단은 사람이 해야 하는 일이다.
		}
	}

	public Task EnableRotationAsync(string keyArn, CancellationToken cancellationToken = default) =>
		client.EnableKeyRotationAsync(new EnableKeyRotationRequest { KeyId = keyArn }, cancellationToken);

	public Task PutKeyPolicyAsync(
		string keyArn, string policyJson, CancellationToken cancellationToken = default) =>
		client.PutKeyPolicyAsync(
			new PutKeyPolicyRequest { KeyId = keyArn, PolicyName = "default", Policy = policyJson },
			cancellationToken);

	public Task TagKeyAsync(
		string keyArn, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default) =>
		client.TagResourceAsync(new TagResourceRequest
		{
			KeyId = keyArn,
			Tags = tags.Select(kv => new Tag { TagKey = kv.Key, TagValue = kv.Value }).ToList(),
		}, cancellationToken);
}
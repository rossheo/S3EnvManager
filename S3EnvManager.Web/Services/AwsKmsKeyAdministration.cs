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

			// DescribeKey는 Disabled/PendingDeletion 키에도 성공한다 - 별칭이 삭제 예약된 키를 계속
			// 가리키고 있으면(레지스트리에서 제거된 뒤 별칭만 못 지운 경우) 재프로비저닝이 죽은 키를
			// "이미 있음"으로 오인해 되살리려 든다. Enabled가 아니면 없는 것으로 취급해야
			// 새 CMK 생성으로 자연히 넘어간다.
			return response.KeyMetadata.KeyState == KeyState.Enabled ? response.KeyMetadata.Arn : null;
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
			// 별칭이 이미 있다. 가리키는 키가 아직 Enabled라면(운영자가 콘솔에서 다른 키로 바꿔둔
			// 경우 등) 그대로 둔다 - 어떤 별칭이 "진짜"인지는 사람이 판단할 일이다. 하지만 Enabled가
			// 아니면(FindKeyArnByAliasAsync가 이걸 "없음"으로 보고 우릴 여기까지 불러온 것이다) 죽은
			// 키를 계속 가리키게 놔두면, 다음 프로비저닝도 매번 이 죽은 별칭을 못 찾고 새 키를 만들어
			// 별칭 없이 고아로 남기는 걸 반복한다 - 방금 만든 키로 옮겨 붙여 그 고리를 끊는다.
			var current = await client.DescribeKeyAsync(new DescribeKeyRequest { KeyId = alias }, cancellationToken)
				.ConfigureAwait(false);
			if (current.KeyMetadata.KeyState != KeyState.Enabled)
			{
				await client.UpdateAliasAsync(
					new UpdateAliasRequest { AliasName = alias, TargetKeyId = keyArn }, cancellationToken)
					.ConfigureAwait(false);
			}
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

	public Task ScheduleDeletionAsync(
		string keyArn, Int32 pendingWindowInDays, CancellationToken cancellationToken = default) =>
		client.ScheduleKeyDeletionAsync(new ScheduleKeyDeletionRequest
		{
			KeyId = keyArn,
			PendingWindowInDays = pendingWindowInDays,
		}, cancellationToken);
}
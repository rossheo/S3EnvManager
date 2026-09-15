using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>실 KMS 없이 CMK 수명주기(생성/별칭/로테이션/키 정책/태깅)를 흉내낸다.</summary>
public sealed class FakeKmsKeyAdministration : IKmsKeyAdministration
{
	public sealed class KeyRecord
	{
		public required string Arn;
		public string? Description;
		public DateTimeOffset CreatedAt;
		public bool RotationEnabled;
		public string? Policy;
		public readonly Dictionary<string, string> Tags = [];
	}

	private readonly Dictionary<string, string> aliasToArn = [];
	private readonly Dictionary<string, KeyRecord> keys = [];
	private readonly HashSet<string> deletionScheduledArns = [];

	public IReadOnlyDictionary<string, KeyRecord> Keys => keys;

	public IReadOnlySet<string> DeletionScheduledArns => deletionScheduledArns;

	// 실 KMS의 DescribeKey는 Disabled/PendingDeletion 키에도 성공하므로(AwsKmsKeyAdministration의
	// KeyState 가드가 실제로 지키는 게 이거다), 삭제 예약된 ARN을 "Enabled 아님"으로 재현한다.
	public Task<string?> FindKeyArnByAliasAsync(string alias, CancellationToken cancellationToken = default)
	{
		if (!aliasToArn.TryGetValue(alias, out var arn) || deletionScheduledArns.Contains(arn))
		{
			return Task.FromResult<string?>(null);
		}
		return Task.FromResult<string?>(arn);
	}

	public Task<string> CreateKeyAsync(
		string description, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
	{
		// CmkRegistrations.Arn 전역 유니크 인덱스 때문에 순번 대신 GUID 기반으로 만든다.
		var arn = $"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";
		var record = new KeyRecord { Arn = arn, Description = description, CreatedAt = DateTimeOffset.UtcNow };
		foreach (var (key, value) in tags)
		{
			record.Tags[key] = value;
		}
		keys[arn] = record;
		return Task.FromResult(arn);
	}

	public Task EnsureAliasAsync(string alias, string keyArn, CancellationToken cancellationToken = default)
	{
		// AwsKmsKeyAdministration과 동일하게: 별칭이 없으면 새로 걸고, 이미 있으면 가리키는 키가
		// 더 이상 Enabled가 아닐 때만(죽은 키) 방금 만든 키로 옮겨 붙인다.
		if (!aliasToArn.TryGetValue(alias, out var current) || deletionScheduledArns.Contains(current))
		{
			aliasToArn[alias] = keyArn;
		}
		return Task.CompletedTask;
	}

	public Task EnableRotationAsync(string keyArn, CancellationToken cancellationToken = default)
	{
		keys[keyArn].RotationEnabled = true;
		return Task.CompletedTask;
	}

	public Task PutKeyPolicyAsync(string keyArn, string policyJson, CancellationToken cancellationToken = default)
	{
		keys[keyArn].Policy = policyJson;
		return Task.CompletedTask;
	}

	public Task TagKeyAsync(
		string keyArn, IReadOnlyDictionary<string, string> tags, CancellationToken cancellationToken = default)
	{
		// 실 kms:TagResource는 CreateKey를 거치지 않은 키에도 동작하므로 여기서도 재현한다.
		if (!keys.TryGetValue(keyArn, out var record))
		{
			record = new KeyRecord { Arn = keyArn, CreatedAt = DateTimeOffset.UtcNow };
			keys[keyArn] = record;
		}
		foreach (var (key, value) in tags)
		{
			record.Tags[key] = value;
		}
		return Task.CompletedTask;
	}

	public Task ScheduleDeletionAsync(
		string keyArn, Int32 pendingWindowInDays, CancellationToken cancellationToken = default)
	{
		deletionScheduledArns.Add(keyArn);
		return Task.CompletedTask;
	}
}

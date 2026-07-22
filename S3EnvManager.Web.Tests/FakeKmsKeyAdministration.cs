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

	public IReadOnlyDictionary<string, KeyRecord> Keys => keys;

	public Task<string?> FindKeyArnByAliasAsync(string alias, CancellationToken cancellationToken = default) =>
		Task.FromResult(aliasToArn.TryGetValue(alias, out var arn) ? arn : null);

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
		if (!aliasToArn.ContainsKey(alias))
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
}

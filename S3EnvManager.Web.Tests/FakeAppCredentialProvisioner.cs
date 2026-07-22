using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>실 IAM 없이 앱별 자격증명 발급/폐기/정책 재적용을 흉내낸다. PutUserPolicy로 전달된
/// CMK ARN 목록을 캡처해 실 IAM 왕복 없이 정책 내용을 검증할 수 있다.</summary>
public sealed class FakeAppCredentialProvisioner : IAppCredentialProvisioner
{
	public sealed class AppUser
	{
		public bool Exists;
		public string? Bucket;
		public IReadOnlyCollection<string> AppFacingCmkArns = [];
		public readonly List<string> AccessKeyIds = [];
	}

	private readonly Dictionary<string, AppUser> users = [];

	public IReadOnlyDictionary<string, AppUser> Users => users;

	private AppUser GetOrCreate(string appName)
	{
		if (!users.TryGetValue(appName, out var user))
		{
			user = new AppUser();
			users[appName] = user;
		}
		return user;
	}

	public Task<ProvisionedCredential> IssueAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default)
	{
		var user = GetOrCreate(appName);
		user.Exists = true;
		user.Bucket = bucket;
		user.AppFacingCmkArns = appFacingCmkArns;
		// DB 유니크 제약(AppCredentials.AccessKeyId) 때문에 프로세스 재시작 간에도 유일해야
		// 하므로 순번 대신 GUID 기반으로 만든다.
		var keyId = "AKIAFAKE" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
		user.AccessKeyIds.Add(keyId);
		return Task.FromResult(new ProvisionedCredential(keyId, "fake-secret-" + keyId));
	}

	public Task ReapplyPolicyAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default)
	{
		if (users.TryGetValue(appName, out var user) && user.Exists)
		{
			user.Bucket = bucket;
			user.AppFacingCmkArns = appFacingCmkArns;
		}
		return Task.CompletedTask;
	}

	public Task RevokeAccessKeyAsync(
		string appName, string accessKeyId, CancellationToken cancellationToken = default)
	{
		if (users.TryGetValue(appName, out var user))
		{
			user.AccessKeyIds.Remove(accessKeyId);
		}
		return Task.CompletedTask;
	}

	public Task DeleteUserAsync(string appName, CancellationToken cancellationToken = default)
	{
		users.Remove(appName);
		return Task.CompletedTask;
	}
}

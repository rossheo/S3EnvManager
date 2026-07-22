using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>실 IAM 없이 부트스트랩 app identity 관리를 흉내낸다.</summary>
public sealed class FakeBootstrapAppIdentityProvisioner : IBootstrapAppIdentityProvisioner
{
	public bool UserProvisioned { get; private set; }
	public IReadOnlyCollection<string>? PolicyAppCmkArns { get; private set; }

	private readonly List<string> accessKeyIds = [];

	public Task<string> EnsureUserAsync(CancellationToken cancellationToken = default)
	{
		UserProvisioned = true;
		return Task.FromResult("arn:aws:iam::000000000000:user/s3envmanager-app");
	}

	public Task PutPolicyAsync(IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default)
	{
		if (!UserProvisioned)
		{
			throw new InvalidOperationException("부트스트랩 app 사용자가 아직 없습니다.");
		}
		PolicyAppCmkArns = appCmkArns;
		return Task.CompletedTask;
	}

	public async Task<bool> TryPutPolicyIfProvisionedAsync(
		IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default)
	{
		if (!UserProvisioned)
		{
			return false;
		}
		await PutPolicyAsync(appCmkArns, cancellationToken).ConfigureAwait(false);
		return true;
	}

	public Task<ProvisionedCredential> IssueAccessKeyAsync(CancellationToken cancellationToken = default)
	{
		var keyId = "AKIABOOTSTRAP" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant();
		accessKeyIds.Add(keyId);
		return Task.FromResult(new ProvisionedCredential(keyId, "fake-secret-" + keyId));
	}

	public Task<IReadOnlyList<string>> ListAccessKeyIdsAsync(CancellationToken cancellationToken = default) =>
		Task.FromResult<IReadOnlyList<string>>([.. accessKeyIds]);

	public Task DeleteAccessKeyAsync(string accessKeyId, CancellationToken cancellationToken = default)
	{
		accessKeyIds.Remove(accessKeyId);
		return Task.CompletedTask;
	}
}

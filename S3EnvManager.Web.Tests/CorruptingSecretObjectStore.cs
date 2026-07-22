using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>N번째 GetCurrentAsync 호출에서만 내용을 깨뜨려, SaveAsync의 저장 검증 실패 →
/// 롤백 경로를 재현한다.</summary>
public sealed class CorruptingSecretObjectStore(ISecretObjectStore inner, Int32 corruptOnCallNumber)
	: ISecretObjectStore
{
	private Int32 _callCount;

	public async Task<StoredSecretObject?> GetCurrentAsync(
		string bucket, string key, CancellationToken cancellationToken = default)
	{
		var result = await inner.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		var callNumber = Interlocked.Increment(ref _callCount);
		if (result is not null && callNumber == corruptOnCallNumber)
		{
			return result with { Content = "FOO=this-is-not-a-valid-sops-file\n" };
		}
		return result;
	}

	public Task<PutSecretObjectResult> PutAsync(
		string bucket, string key, string content, string? actorEmail = null,
		CancellationToken cancellationToken = default) =>
		inner.PutAsync(bucket, key, content, actorEmail, cancellationToken);

	public Task RestoreVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
		inner.RestoreVersionAsync(bucket, key, versionId, cancellationToken);

	public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
		inner.DeleteAsync(bucket, key, cancellationToken);

	public Task<List<SecretObjectVersion>> ListVersionsAsync(
		string bucket, string key, bool includeActorEmail = false, CancellationToken cancellationToken = default) =>
		inner.ListVersionsAsync(bucket, key, includeActorEmail, cancellationToken);

	public Task<string> GetVersionContentAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
		inner.GetVersionContentAsync(bucket, key, versionId, cancellationToken);

	public Task DeleteVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
		inner.DeleteVersionAsync(bucket, key, versionId, cancellationToken);
}
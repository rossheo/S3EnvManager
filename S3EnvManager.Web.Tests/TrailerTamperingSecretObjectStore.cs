using S3EnvManager.Sops;
using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>N번째 GetCurrentAsync 호출에서만 KMS 트레일러의 admin ARN을 바꿔치기해, 값/MAC은
/// 멀쩡하지만 트레일러가 손상된 번들을 재현한다 - SaveAsync의 저장 직후 검증이 KMS를 다시 부르지
/// 않고 로컬 데이터 키로만 검증하게 되면서, 트레일러 자체의 손상은 값 비교만으로는 못 잡는다는
/// 회귀를 잡기 위한 것이다.</summary>
public sealed class TrailerTamperingSecretObjectStore(ISecretObjectStore inner, Int32 tamperOnCallNumber)
	: ISecretObjectStore
{
	private Int32 _callCount;

	public async Task<StoredSecretObject?> GetCurrentAsync(
		string bucket, string key, CancellationToken cancellationToken = default)
	{
		var result = await inner.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		var callNumber = Interlocked.Increment(ref _callCount);
		if (result is not null && callNumber == tamperOnCallNumber)
		{
			var document = SopsDotEnvDocument.Parse(result.Content);
			document.KmsEntries[0] = document.KmsEntries[0] with { Arn = document.KmsEntries[0].Arn + "-tampered" };
			return result with { Content = document.Serialize() };
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

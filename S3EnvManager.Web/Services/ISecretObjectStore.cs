namespace S3EnvManager.Web.Services;

public sealed record StoredSecretObject(string Content, string ETag, string? VersionId);

public sealed record PutSecretObjectResult(string ETag, string? VersionId);

// ActorEmail은 CMK 재래핑 등 시스템 작업이 쓴 버전이거나 이 기능 추가 이전 버전은 null.
public sealed record SecretObjectVersion(
	string VersionId, bool IsLatest, DateTimeOffset LastModified, string? ActorEmail = null);

public interface ISecretObjectStore
{
	Task<StoredSecretObject?> GetCurrentAsync(
		string bucket, string key, CancellationToken cancellationToken = default);

	Task<PutSecretObjectResult> PutAsync(
		string bucket, string key, string content, string? actorEmail = null,
		CancellationToken cancellationToken = default);

	// 저장 검증 실패 시 직전 버전을 현재 버전 위에 그대로 복원한다(재암호화 없이 CopyObject).
	Task RestoreVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default);

	// 직전 버전이 없을 때(최초 저장 검증 실패)의 폴백 - 방금 쓴 손상된 오브젝트를 지운다.
	Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default);

	// S3 버전은 불변이라 noncurrent 버전의 admin 엔트리는 제자리 재래핑이 불가능하다 - CMK
	// 제거 시 그 버전을 참조하는 버전 자체를 지우는 수밖에 없다. includeActorEmail은 버전마다
	// HEAD 요청이 추가되므로 저장 히스토리 화면에서만 켠다.
	Task<List<SecretObjectVersion>> ListVersionsAsync(
		string bucket, string key, bool includeActorEmail = false, CancellationToken cancellationToken = default);

	Task<string> GetVersionContentAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default);

	// 파괴적 작업 - 그 버전으로의 롤백이 다시는 불가능해진다. 현재 버전을 넘기면 안 된다(호출자 책임).
	Task DeleteVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default);
}
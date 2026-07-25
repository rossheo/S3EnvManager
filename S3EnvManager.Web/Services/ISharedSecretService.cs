namespace S3EnvManager.Web.Services;

public sealed record SharedSecretSummary(
	Guid Id, string Name, string? Description, DateTimeOffset? ExpiresAt, DateTimeOffset UpdatedAt);

public sealed record SharedSecretDetail(
	Guid Id, string Name, string? Description, string Value, DateTimeOffset? ExpiresAt);

// Env/kind/키이름별 cascade 재materialize 결과. Phase 1에서는 항상 빈 목록(참조가 아직 없음).
public sealed record SharedSecretCascadeFailure(Guid EnvId, bool IsOverwriteBundle, string Reason);

public sealed record SharedSecretUpdateResult(IReadOnlyList<SharedSecretCascadeFailure> Failures);

public interface ISharedSecretService
{
	Task<List<SharedSecretSummary>> ListAsync(CancellationToken cancellationToken = default);

	// 값은 복호화해서 관리자 편집 화면 진입 시에만 반환한다(감사 로그엔 남기지 않음).
	Task<SharedSecretDetail> LoadForEditAsync(Guid id, CancellationToken cancellationToken = default);

	Task<Guid> CreateAsync(
		string name, string? description, string value, DateTimeOffset? expiresAt,
		string? actorUserId, CancellationToken cancellationToken = default);

	// newValue가 null이면 값은 바꾸지 않고 description/만료일만 갱신한다(cascade 재materialize도
	// 스킵). newValue가 지정되면 참조하는 모든 App/Env로 값을 재전파한다.
	Task<SharedSecretUpdateResult> UpdateAsync(
		Guid id, string? description, string? newValue, DateTimeOffset? expiresAt,
		string? actorUserId, CancellationToken cancellationToken = default);

	// 참조가 남아있으면 자동으로 전부 detach(자체 소유 키로 전환)한 뒤 레지스트리에서 삭제한다.
	// 참조가 있다고 예외를 던져 사용자를 막지 않는다.
	Task DeleteAsync(Guid id, string? actorUserId, CancellationToken cancellationToken = default);

	// Administrator만 호출 - 어느 App이 이 SharedSecret을 참조로 추가할 수 있는지 화이트리스트.
	Task GrantAsync(
		Guid sharedSecretId, Guid appId, string? actorUserId, CancellationToken cancellationToken = default);

	// DeleteAsync와 대칭 - 그 App이 이미 가진 참조가 있으면 먼저 전부 detach(자체 소유 키로
	// 전환)한 뒤 그랜트를 제거한다. "그랜트 없음 + 참조는 있음"이라는 반쪽 상태를 남기지 않는다.
	Task RevokeGrantAsync(
		Guid sharedSecretId, Guid appId, string? actorUserId, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<Guid>> ListGrantedAppIdsAsync(
		Guid sharedSecretId, CancellationToken cancellationToken = default);

	Task<IReadOnlyList<SharedSecretReferenceInfo>> ListReferencesAsync(
		Guid sharedSecretId, CancellationToken cancellationToken = default);
}

public sealed record SharedSecretReferenceInfo(
	Guid EnvId, string AppName, string EnvName, bool IsOverwriteBundle, string KeyName,
	DateTimeOffset LastMaterializedAt);

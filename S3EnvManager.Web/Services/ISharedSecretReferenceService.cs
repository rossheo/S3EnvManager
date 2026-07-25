namespace S3EnvManager.Web.Services;

public interface ISharedSecretReferenceService
{
	// 이 App이 그랜트받은(참조로 추가할 수 있는) SharedSecret 목록.
	Task<IReadOnlyList<SharedSecretSummary>> ListReferencableAsync(
		Guid appId, CancellationToken cancellationToken = default);

	// 현재 (Env, kind)의 참조 슬롯: 키 이름 -> SharedSecretId.
	Task<IReadOnlyDictionary<string, Guid>> LoadReferencesAsync(
		Guid envId, SecretBundleKind kind, CancellationToken cancellationToken = default);

	// 참조 키의 실제 값은 클라이언트에서 받지 않는다 - 서버가 그랜트를 검증하고 레지스트리에서
	// 값을 다시 읽어 자체 소유 값과 병합한 뒤 ISecretBundleService.SaveAsync를 호출한다.
	// editedReferences에 없는 SharedSecretId를 가진 그랜트되지 않은 App이 있으면 저장을 거부한다.
	Task<SaveOutcome> SaveWithReferencesAsync(
		Guid envId,
		IReadOnlyDictionary<string, string> baseSnapshot,
		string? baseETag,
		IReadOnlyDictionary<string, string> editedOwnValues,
		IReadOnlyDictionary<string, Guid> editedReferences,
		string? actorUserId,
		string? actorEmail,
		SecretBundleKind kind,
		IReadOnlyDictionary<string, DateTimeOffset?>? editedOwnExpirations = null,
		CancellationToken cancellationToken = default);

	// 사용자가 명시적으로 "연결 해제"를 눌렀을 때 - 값은 이미 번들에 materialize돼 있으므로
	// 참조 행만 지우면 그 키는 평범한 자체 소유 키가 된다(재저장 불필요).
	Task DetachAsync(
		Guid envId, bool isOverwriteBundle, string keyName, string? actorUserId,
		CancellationToken cancellationToken = default);
}

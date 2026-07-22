namespace S3EnvManager.Web.Services;

// BaseETag가 null이면 아직 한 번도 저장된 적 없는(오브젝트가 존재하지 않는) 환경이다.
public sealed record SecretEditSession(IReadOnlyDictionary<string, string> Values, string? BaseETag);

public abstract record SaveOutcome;

public sealed record SaveSuccess(string NewETag) : SaveOutcome;

// RealConflicts가 비어 있지 않으면 사용자가 값을 골라야 한다. 재저장 시에는 TheirsSnapshot/TheirsETag를
// 새 base로 써서 다시 SaveAsync를 호출한다.
public sealed record SaveConflict(
	IReadOnlyDictionary<string, string> MergedValues,
	IReadOnlyList<string> AutoAppliedTheirsKeys,
	IReadOnlyList<ConflictItem> RealConflicts,
	IReadOnlyDictionary<string, string> TheirsSnapshot,
	string? TheirsETag) : SaveOutcome;

// 저장 검증(복호화 재확인) 실패로 이전 버전으로 되돌렸다 - last_known_etag는 갱신되지 않았다.
public sealed record SaveFailed(string Reason) : SaveOutcome;
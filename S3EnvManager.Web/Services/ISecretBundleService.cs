using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

// base 번들(`{app}/{env}.env`, 1단)과 overwrite 번들(`{app}/{env}.overwrite.env`, 3단 최종
// 강제 override). 로컬 env var(2단)는 S3EnvManager가 관여하지 않는다.
public enum SecretBundleKind
{
	Base,
	Overwrite,
}

public interface ISecretBundleService
{
	Task<SecretEditSession> LoadForEditAsync(
		Guid envId, SecretBundleKind kind = SecretBundleKind.Base, CancellationToken cancellationToken = default);

	// 저장 성공 시 감사 로그에는 어떤 키가 추가/변경/삭제됐는지만 남기고 값 자체는 남기지 않는다.
	Task<SaveOutcome> SaveAsync(
		Guid envId,
		IReadOnlyDictionary<string, string> baseSnapshot,
		string? baseETag,
		IReadOnlyDictionary<string, string> editedValues,
		string? actorUserId = null,
		string? actorEmail = null,
		SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default);

	// 값은 복호화하지 않으므로, 오래된 버전을 감쌌던 CMK가 제거되어 있어도 실패하지 않는다.
	Task<IReadOnlyList<SecretObjectVersion>> ListHistoryAsync(
		Guid envId, SecretBundleKind kind = SecretBundleKind.Base, CancellationToken cancellationToken = default);

	// 그 버전이 이미 제거된 CMK로 감싸져 있으면 KMS 호출이 실패한다 - 호출자가 잡아서 안내해야 한다.
	Task<IReadOnlyDictionary<string, string>> LoadVersionAsync(
		Guid envId, string versionId, SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default);

	// 복호화하지 않고 항목 수만 세므로 CMK가 제거되어 있어도 실패하지 않는다. app/env를 호출자가
	// 들고 있는 상태로 받아 DB 조회 없이 S3만 호출한다(반복 호출 시 DbContext 동시성 회피).
	Task<Int32> GetKeyCountAsync(
		App app, Env env, SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default);
}
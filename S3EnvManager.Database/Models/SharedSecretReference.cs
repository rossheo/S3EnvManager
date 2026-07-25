namespace S3EnvManager.Database.Models;

/// <summary>특정 (Env, kind, KeyName) 슬롯이 SharedSecret을 참조 중임을 나타내는 메타데이터.
/// 값 자체는 여기 없다 - 그 Env의 시크릿 번들 안에 이미 그 App 자신의 envelope으로 구워져
/// (materialize) 있다. 이 행은 "누가 무엇을 참조하는지"와 "마지막으로 언제 동기화됐는지"만
/// 추적한다.</summary>
public class SharedSecretReference
{
	public Guid Id { get; init; }

	public Guid SharedSecretId { get; init; }

	public SharedSecret? SharedSecret { get; init; }

	public Guid EnvId { get; init; }

	public Env? Env { get; init; }

	// base(false) / overwrite(true) - KeyExpiration과 동일 관례.
	public bool IsOverwriteBundle { get; init; }

	public required string KeyName { get; init; }

	/// <summary>이 참조가 마지막으로 SharedSecret의 값을 반영해 저장에 성공한 시각. 이 값이
	/// SharedSecret.UpdatedAt보다 오래됐으면 "뒤처짐"(cascade 실패 후 아직 재동기화 안 됨)이다.</summary>
	public DateTimeOffset LastMaterializedAt { get; set; }

	public DateTimeOffset CreatedAt { get; init; }

	public string? CreatedByUserId { get; set; }
}

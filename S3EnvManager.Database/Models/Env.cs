namespace S3EnvManager.Database.Models;

/// <summary>S3의 `{app}/{env}.env` 오브젝트(시크릿 번들) 하나에 대응한다.</summary>
public class Env
{
	public Guid Id { get; init; }

	public Guid AppId { get; init; }

	public App? App { get; init; }

	public required EnvName Name { get; init; }

	/// <summary>직전 저장 성공 시점의 S3 ETag - 편집 세션 시작 시점 값과 비교해 동시 편집
	/// 충돌을 감지한다.</summary>
	public string? LastKnownETag { get; set; }

	/// <summary>overwrite 번들(`{app}/{env}.overwrite.env`)은 별개의 S3 오브젝트라 독립적으로
	/// 추적한다.</summary>
	public string? OverwriteLastKnownETag { get; set; }
}
namespace S3EnvManager.Database.Models;

/// <summary>이 테이블에는 만료일을 지정한 키만 행으로 남는다(sparse, FeatureSwitch와 동일한 관례).</summary>
public class KeyExpiration
{
	public Guid Id { get; init; }

	public Guid EnvId { get; init; }

	public Env? Env { get; init; }

	// base(false) / overwrite(true) 번들 구분.
	public bool IsOverwriteBundle { get; init; }

	public required string KeyName { get; init; }

	public required DateTimeOffset ExpiresAt { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

namespace S3EnvManager.Database.Models;

public class App
{
	public Guid Id { get; init; }

	public required string Name { get; set; }

	public DateTimeOffset CreatedAt { get; init; }

	/// <summary>지연 삭제(soft delete) 기준 시각 - null이면 삭제되지 않은 App. 60일 후 S3
	/// 오브젝트가 실제로 지워진다.</summary>
	public DateTimeOffset? DeletedAt { get; set; }

	/// <summary>S3 오브젝트가 실제로 지워진 시각 - null이면 아직 미실행(재실행 시 중복 삭제
	/// 방지 마커).</summary>
	public DateTimeOffset? PurgedAt { get; set; }

	public List<Env> Envs { get; init; } = [];
}
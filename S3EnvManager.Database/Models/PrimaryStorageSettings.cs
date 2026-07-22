namespace S3EnvManager.Database.Models;

/// <summary>자동 프로비저닝이 유일한 설정 경로다 - admin 부트스트랩 자격증명을 그대로 쓰므로
/// 별도 자격증명 저장이 없다.</summary>
public class PrimaryStorageSettings
{
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

	public Guid Id { get; init; } = SingletonId;

	public string? Region { get; set; }

	/// <summary>모든 App이 공유하는 주 저장소 버킷 이름 - 아직 프로비저닝하지 않았으면 null.</summary>
	public string? Bucket { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}

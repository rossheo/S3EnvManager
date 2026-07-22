namespace S3EnvManager.Database.Models;

/// <summary>싱글턴 설정 테이블 - <see cref="Id"/>는 항상 <see cref="SingletonId"/> 고정값이다.</summary>
public class DataKeyRotationSettings
{
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

	public Guid Id { get; init; } = SingletonId;

	/// <summary>1~3650(최대 10년) 범위. 기본값 14일.</summary>
	public required Int32 RotationIntervalDays { get; set; }

	public DateTimeOffset UpdatedAt { get; set; }
}
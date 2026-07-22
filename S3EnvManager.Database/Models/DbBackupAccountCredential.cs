namespace S3EnvManager.Database.Models;

/// <summary>pg_dump 전용 읽기 전용 Postgres 역할의 현재 비밀번호를 담는 싱글턴 행 - 역할
/// 자체는 DB 안에 있고, 이 테이블은 운영자가 화면에서 재조회할 수 있도록 암호화해 보관한다.</summary>
public class DbBackupAccountCredential
{
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

	public Guid Id { get; init; } = SingletonId;

	public required string Username { get; set; }

	public required byte[] EncryptedPassword { get; set; }

	public required Guid DataKeyId { get; set; }

	public DateTimeOffset RotatedAt { get; set; }
}
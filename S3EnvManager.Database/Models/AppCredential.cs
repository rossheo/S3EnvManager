namespace S3EnvManager.Database.Models;

/// <summary>SecretKey는 평문이 아니라 KMS envelope encryption 기반 데이터 키
/// (<see cref="DataKeyGeneration"/>)로 암호화해 저장한다.</summary>
public class AppCredential
{
	public Guid Id { get; init; }

	public Guid AppId { get; init; }

	public App? App { get; init; }

	public required string AccessKeyId { get; init; }

	public required byte[] EncryptedSecretKey { get; init; }

	public Guid DataKeyId { get; init; }

	public DataKeyGeneration? DataKey { get; init; }

	public DateTimeOffset IssuedAt { get; init; }

	/// <summary>null이면 아직 활성 상태.</summary>
	public DateTimeOffset? RevokedAt { get; set; }
}
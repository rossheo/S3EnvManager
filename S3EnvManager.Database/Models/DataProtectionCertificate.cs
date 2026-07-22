namespace S3EnvManager.Database.Models;

/// <summary>PFX 비밀번호는 DB 밖(환경변수/Aspire 파라미터)에서만 관리해 DB 단독 유출로는
/// 복호화가 불가능하게 한다. Append-only - 과거 세대로 암호화된 값을 계속 복호화하려면
/// 오래된 로우도 절대 지우지 않는다.</summary>
public class DataProtectionCertificate
{
	public Guid Id { get; init; }

	public required byte[] Pkcs12 { get; init; }

	public required string Thumbprint { get; init; }

	public DateTimeOffset NotBefore { get; init; }

	public DateTimeOffset NotAfter { get; init; }

	public DateTimeOffset CreatedAt { get; init; }
}
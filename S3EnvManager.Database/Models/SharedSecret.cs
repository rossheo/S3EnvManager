namespace S3EnvManager.Database.Models;

/// <summary>여러 App이 공유하는 시크릿(외부 API 키 등) 레지스트리 본체. 값은 App 이름에 묶이지
/// 않는 admin-only envelope(<see cref="DataKeyGeneration"/> 세대 기반, App 자격증명 SecretAccessKey를
/// 감싸는 것과 동일한 방식)으로 암호화되고, 참조하는 각 App의 실제 시크릿 번들에는 저장 시점에
/// 그 App 자신의 envelope으로 다시 암호화되어(materialize) 구워 넣어진다.</summary>
public class SharedSecret
{
	public Guid Id { get; init; }

	public required string Name { get; set; }

	public string? Description { get; set; }

	public required byte[] Ciphertext { get; set; }

	public required Guid DataKeyId { get; set; }

	public DataKeyGeneration? DataKey { get; init; }

	/// <summary>참조하는 Env마다 개별 만료일을 두지 않고 여기 하나만 둔다 - 갱신 한 번으로
	/// 끝난다는 전제와 일치하고, 참조하는 App 수만큼 중복 알림이 뜨는 것을 막는다.</summary>
	public DateTimeOffset? ExpiresAt { get; set; }

	public DateTimeOffset CreatedAt { get; init; }

	public DateTimeOffset UpdatedAt { get; set; }

	public string? CreatedByUserId { get; set; }
}

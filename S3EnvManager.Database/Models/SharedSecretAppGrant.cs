namespace S3EnvManager.Database.Models;

/// <summary>어느 App이 어느 SharedSecret을 참조로 추가할 수 있는지에 대한 화이트리스트.
/// Administrator만 추가/제거하며, 참조 추가 시 서버가 반드시 이 화이트리스트를 검증한다.</summary>
public class SharedSecretAppGrant
{
	public Guid Id { get; init; }

	public Guid SharedSecretId { get; init; }

	public SharedSecret? SharedSecret { get; init; }

	public Guid AppId { get; init; }

	public App? App { get; init; }

	public DateTimeOffset GrantedAt { get; init; }

	public string? GrantedByUserId { get; set; }
}

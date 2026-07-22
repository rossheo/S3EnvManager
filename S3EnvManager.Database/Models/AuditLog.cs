namespace S3EnvManager.Database.Models;

/// <summary>다양한 이벤트 종류를 하나의 테이블에 담기 위해 <see cref="EventType"/> 문자열 +
/// 자유 형식 <see cref="Details"/>로 느슨하게 구조화한다.</summary>
public class AuditLog
{
	public Guid Id { get; init; }

	public DateTimeOffset OccurredAt { get; init; }

	/// <summary>시스템이 스스로 일으킨 이벤트(예: 자가 치유)는 null.</summary>
	public string? ActorUserId { get; init; }

	public required string EventType { get; init; }

	/// <summary>특정 App에 관련된 이벤트에만 설정된다.</summary>
	public Guid? AppId { get; init; }

	public string? Details { get; init; }
}
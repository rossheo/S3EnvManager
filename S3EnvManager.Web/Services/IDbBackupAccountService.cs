namespace S3EnvManager.Web.Services;

public sealed record DbBackupAccountInfo(string Username, DateTimeOffset RotatedAt);

// pg_dump 전용 읽기 전용 Postgres 역할을 self-heal로 생성한다. pg_dump 자동화가 장기간 같은
// 자격증명을 참조하므로 예고 없는 자동 회전은 그 자동화를 깨뜨린다 - 회전은 관리자 명시적 요청 시에만.
public interface IDbBackupAccountService
{
	// 최초 설치 시에만 생성한다 - 재기동만으로 비밀번호가 바뀌면 pg_dump가 stale한 값을 쓰게 된다.
	Task EnsureAsync(CancellationToken cancellationToken = default);

	Task RotateNowAsync(CancellationToken cancellationToken = default);

	Task<DbBackupAccountInfo?> GetCurrentAsync(CancellationToken cancellationToken = default);

	Task<string> RevealCurrentPasswordAsync(
		string? actorUserId = null, CancellationToken cancellationToken = default);
}
using Microsoft.EntityFrameworkCore;
using Npgsql;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Tests;

/// <summary>FakeKmsKeyOperations의 래핑 상태는 프로세스 메모리에만 존재하는 반면 KMS 의존
/// 테이블 행은 Postgres에 영속되어, 이전 프로세스가 wrap한 ciphertext는 이번 프로세스의
/// fake로 복호화할 수 없다 - 세션 시작 시 한 번 비운다(static 초기화, 직렬 실행이라 안전).
/// EphemeralDataProtectionProvider로 암호화하는 UserNotificationSettings도 같은 이유로 비운다 -
/// 프로세스마다 새 마스터 키를 쓰므로 이전 프로세스가 Protect()한 웹훅 URL은 이번 프로세스의
/// Unprotect()로 복호화할 수 없다. SharedSecrets는 DataKeyGenerations를 FK Restrict로 참조하므로
/// DataKeyGenerations를 지우기 전에 먼저 비워야 한다.</summary>
internal static class TestEnvironment
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	private static readonly Task<bool> Available = CheckAndResetAsync();

	public static Task<bool> IsPostgresAvailableAsync() => Available;

	private static async Task<bool> CheckAndResetAsync()
	{
		try
		{
			await using var connection = new NpgsqlConnection(PostgresConnectionString);
			await connection.OpenAsync();
		}
		catch
		{
			return false;
		}

		await using var db = new ApplicationDbContext(
			new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DbBackupAccountCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecretReferences\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecretAppGrants\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecrets\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataKeyGenerations\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"UserNotificationSettings\"");
		return true;
	}
}

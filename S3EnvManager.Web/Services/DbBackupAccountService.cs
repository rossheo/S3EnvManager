using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class DbBackupAccountService(
	ApplicationDbContext db, IAppSecretKeyCipher secretKeyCipher, IAuditLogger auditLogger,
	ILogger<DbBackupAccountService> logger)
	: IDbBackupAccountService
{
	private const string RoleName = "s3envmanager_backup_readonly";

	public async Task EnsureAsync(CancellationToken cancellationToken = default)
	{
		// 이미 저장된 자격증명이 있으면 비밀번호는 손대지 않는다 - 재기동마다 회전시키면 pg_dump
		// 자동화가 예고 없이 깨진다. 회전은 RotateNowAsync(관리자의 명시적 요청)로만 일어난다.
		// 다만 GRANT는 멱등적이고 값을 바꾸지 않으므로, 스키마 변경/수동 REVOKE 등으로 권한이
		// 드리프트됐을 때를 대비해 매 기동마다 재확인/재부여한다(self-heal).
		var alreadyExists = await db.DbBackupAccountCredentials
			.AnyAsync(c => c.Id == DbBackupAccountCredential.SingletonId, cancellationToken).ConfigureAwait(false);
		if (alreadyExists)
		{
			// best-effort self-heal이다 - 권한 재부여가 실패해도(일시적 DB 장애, 권한 부족 등)
			// Web 기동 자체를 막을 정도로 치명적이지 않으므로 기록만 하고 계속 진행한다.
			try
			{
				await ReapplyGrantsAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (PostgresException ex)
			{
				logger.LogWarning(ex,
					"backup-readonly 권한 재확인/재부여에 실패했습니다. 다음 기동 때 다시 시도합니다.");
			}
			return;
		}

		await CreateOrRotateAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task RotateNowAsync(CancellationToken cancellationToken = default) =>
		CreateOrRotateAsync(cancellationToken);

	private async Task CreateOrRotateAsync(CancellationToken cancellationToken)
	{
		// admin CMK가 없으면 비밀번호를 암호화 저장할 수 없으니 아예 건너뛴다 - 역할/비밀번호를
		// 만들어놓고 저장 못 하면 운영자가 영영 알 수 없게 된다.
		var hasActiveAdminCmk = await db.CmkRegistrations.AsNoTracking()
			.AnyAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active, cancellationToken)
			.ConfigureAwait(false);
		if (!hasActiveAdminCmk)
		{
			return;
		}

		var roleExists = (await db.Database
			.SqlQueryRaw<Int32>("SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = {0}", RoleName)
			.ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

		// Postgres DDL은 식별자를 바인드 파라미터로 받을 수 없다. RoleName은 코드 상수,
		// password는 base64url 알파벳만 쓰도록 생성해 사용자 입력 삽입 위험이 없으므로
		// EF1002 경고를 의도적으로 끈다.
#pragma warning disable EF1002
		if (!roleExists)
		{
			try
			{
				await db.Database.ExecuteSqlRawAsync($"CREATE ROLE \"{RoleName}\" LOGIN", cancellationToken)
					.ConfigureAwait(false);
			}
			catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DuplicateObject)
			{
				// 여러 인스턴스 동시 기동으로 인한 체크-후-생성 경쟁 - 다른 인스턴스가 이미 생성함.
			}
		}

		var password = GenerateSafePassword();
		await db.Database.ExecuteSqlRawAsync(
			$"ALTER ROLE \"{RoleName}\" WITH PASSWORD '{password}'", cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002

		await ReapplyGrantsAsync(cancellationToken).ConfigureAwait(false);

		var (ciphertext, dataKeyId) = await secretKeyCipher.EncryptAsync(password, cancellationToken)
			.ConfigureAwait(false);
		var existing = await db.DbBackupAccountCredentials
			.SingleOrDefaultAsync(c => c.Id == DbBackupAccountCredential.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		if (existing is null)
		{
			db.DbBackupAccountCredentials.Add(new DbBackupAccountCredential
			{
				Username = RoleName,
				EncryptedPassword = ciphertext,
				DataKeyId = dataKeyId,
				RotatedAt = DateTimeOffset.UtcNow,
			});
		}
		else
		{
			existing.EncryptedPassword = ciphertext;
			existing.DataKeyId = dataKeyId;
			existing.RotatedAt = DateTimeOffset.UtcNow;
		}
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		await auditLogger.LogAsync(AuditEventTypes.DbBackupAccountRotated, actorUserId: null, appId: null,
			details: System.Text.Json.JsonSerializer.Serialize(new { username = RoleName }, AuditJsonOptions.Default),
			cancellationToken).ConfigureAwait(false);
	}

	// 비밀번호는 건드리지 않고 GRANT만 재확인/재부여한다 - 전부 멱등적(재실행해도 값이 바뀌지
	// 않음)이라 매 기동마다 실행해 수동 REVOKE나 스키마 변경으로 인한 권한 드리프트를 self-heal한다.
	private async Task ReapplyGrantsAsync(CancellationToken cancellationToken)
	{
		var roleExists = (await db.Database
			.SqlQueryRaw<Int32>("SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = {0}", RoleName)
			.ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;
		if (!roleExists)
		{
			// 부트스트랩 경로(CreateOrRotateAsync)에서 호출된 경우는 역할을 막 만든 직후라
			// 이 분기를 타지 않는다. alreadyExists 경로에서 여기 도달했다면 역할이 외부에서
			// 삭제됐는데 자격증명 행만 남은, 이미 깨진 상태다 - 여기서 되살리지 않고 조용히
			// 건너뛴다(비밀번호 재발급 없이는 안전하게 복구할 수 없음).
			return;
		}

		var currentDatabase = (await db.Database.SqlQueryRaw<string>("SELECT current_database()")
			.ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

		// Postgres DDL은 식별자를 바인드 파라미터로 받을 수 없다. RoleName은 코드 상수,
		// currentDatabase는 DB에서 조회한 값이라 사용자 입력 삽입 위험이 없으므로 EF1002 경고를
		// 의도적으로 끈다. 시퀀스도 빠지면 pg_dump가 "missing SELECT on ..._seq"로 실패한다
		// (identity 컬럼을 쓰는 테이블은 시퀀스가 별도 오브젝트라 테이블 SELECT만으로는 커버되지 않음).
#pragma warning disable EF1002
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT CONNECT ON DATABASE \"{currentDatabase}\" TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT USAGE ON SCHEMA public TO \"{RoleName}\"", cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{RoleName}\"", cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT SELECT ON ALL SEQUENCES IN SCHEMA public TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON SEQUENCES TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002
	}

	public async Task<DbBackupAccountInfo?> GetCurrentAsync(CancellationToken cancellationToken = default)
	{
		var credential = await db.DbBackupAccountCredentials.AsNoTracking()
			.SingleOrDefaultAsync(c => c.Id == DbBackupAccountCredential.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		return credential is null ? null : new DbBackupAccountInfo(credential.Username, credential.RotatedAt);
	}

	public async Task<string> RevealCurrentPasswordAsync(
		string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var credential = await db.DbBackupAccountCredentials.AsNoTracking()
			.SingleAsync(c => c.Id == DbBackupAccountCredential.SingletonId, cancellationToken).ConfigureAwait(false);
		var password = await secretKeyCipher.DecryptAsync(
			credential.EncryptedPassword, credential.DataKeyId, cancellationToken).ConfigureAwait(false);

		await auditLogger.LogAsync(AuditEventTypes.DbBackupAccountPasswordRevealed, actorUserId, appId: null,
			details: System.Text.Json.JsonSerializer.Serialize(
				new { username = credential.Username }, AuditJsonOptions.Default),
			cancellationToken).ConfigureAwait(false);

		return password;
	}

	private static string GenerateSafePassword()
	{
		Span<byte> bytes = stackalloc byte[32];
		RandomNumberGenerator.Fill(bytes);
		return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').Replace("=", string.Empty);
	}
}
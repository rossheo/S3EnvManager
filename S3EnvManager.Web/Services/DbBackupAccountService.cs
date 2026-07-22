using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class DbBackupAccountService(
	ApplicationDbContext db, IAppSecretKeyCipher secretKeyCipher, IAuditLogger auditLogger)
	: IDbBackupAccountService
{
	private const string RoleName = "s3envmanager_backup_readonly";

	public async Task EnsureAsync(CancellationToken cancellationToken = default)
	{
		// 이미 저장된 자격증명이 있으면 손대지 않는다 - 재기동마다 회전시키면 pg_dump 자동화가
		// 예고 없이 깨진다. 회전은 RotateNowAsync(관리자의 명시적 요청)로만 일어난다.
		var alreadyExists = await db.DbBackupAccountCredentials
			.AnyAsync(c => c.Id == DbBackupAccountCredential.SingletonId, cancellationToken).ConfigureAwait(false);
		if (alreadyExists)
		{
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

		var currentDatabase = (await db.Database.SqlQueryRaw<string>("SELECT current_database()")
			.ToListAsync(cancellationToken).ConfigureAwait(false)).Single();

		var roleExists = (await db.Database
			.SqlQueryRaw<Int32>("SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = {0}", RoleName)
			.ToListAsync(cancellationToken).ConfigureAwait(false)).Count > 0;

		// Postgres DDL은 식별자를 바인드 파라미터로 받을 수 없다. RoleName은 코드 상수,
		// currentDatabase는 DB에서 조회한 값, password는 base64url 알파벳만 쓰도록 생성해
		// 사용자 입력 삽입 위험이 없으므로 EF1002 경고를 의도적으로 끈다.
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

		// pg_dump에 필요한 최소 권한.
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT CONNECT ON DATABASE \"{currentDatabase}\" TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT USAGE ON SCHEMA public TO \"{RoleName}\"", cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"GRANT SELECT ON ALL TABLES IN SCHEMA public TO \"{RoleName}\"", cancellationToken).ConfigureAwait(false);
		await db.Database.ExecuteSqlRawAsync(
			$"ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO \"{RoleName}\"",
			cancellationToken).ConfigureAwait(false);
#pragma warning restore EF1002

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
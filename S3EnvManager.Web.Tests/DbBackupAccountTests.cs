using Microsoft.EntityFrameworkCore;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>Postgres 권한/GRANT/인증 자체를 검증하는 테스트라 AWS와 무관하다(KMS는
/// 비밀번호 암호화에만 부수적으로 쓰여 fake로 대체).</summary>
public class DbBackupAccountTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task EnsureAsync_CreatesReadOnlyRole_ThatCanSelectButNotWrite()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await EnsureActiveAdminCmkAsync(db);

		var service = new DbBackupAccountService(
			CreateDbContext(), CreateSecretKeyCipher(kms), new AuditLogger(CreateDbContext()));
		await service.EnsureAsync();

		var info = await service.GetCurrentAsync();
		Assert.NotNull(info);
		var password = await service.RevealCurrentPasswordAsync();

		var builder = new NpgsqlConnectionStringBuilder(PostgresConnectionString)
		{
			Username = info!.Username,
			Password = password,
		};
		await using var readOnlyConnection = new NpgsqlConnection(builder.ConnectionString);
		await readOnlyConnection.OpenAsync();

		await using (var selectCmd = new NpgsqlCommand("SELECT COUNT(*) FROM \"Apps\"", readOnlyConnection))
		{
			var count = await selectCmd.ExecuteScalarAsync();
			Assert.NotNull(count);
		}

		await using (var insertCmd = new NpgsqlCommand(
			"INSERT INTO \"Apps\" (\"Id\", \"Name\", \"Bucket\", \"CreatedAt\") VALUES (gen_random_uuid(), 'should-fail', 'x', now())",
			readOnlyConnection))
		{
			await Assert.ThrowsAsync<PostgresException>(() => insertCmd.ExecuteNonQueryAsync());
		}
	}

	[Fact]
	public async Task RotateNowAsync_RotatesPassword_OldPasswordStopsWorking()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await EnsureActiveAdminCmkAsync(db);

		var service = new DbBackupAccountService(
			CreateDbContext(), CreateSecretKeyCipher(kms), new AuditLogger(CreateDbContext()));
		await service.RotateNowAsync();
		var info1 = await service.GetCurrentAsync();
		var password1 = await service.RevealCurrentPasswordAsync();

		await service.RotateNowAsync();
		var info2 = await service.GetCurrentAsync();
		var password2 = await service.RevealCurrentPasswordAsync();

		Assert.Equal(info1!.Username, info2!.Username);
		Assert.NotEqual(password1, password2);
		Assert.True(info2.RotatedAt >= info1.RotatedAt);

		var oldBuilder = new NpgsqlConnectionStringBuilder(PostgresConnectionString)
			{ Username = info1.Username, Password = password1 };
		await using var oldConnection = new NpgsqlConnection(oldBuilder.ConnectionString);
		await Assert.ThrowsAsync<PostgresException>(() => oldConnection.OpenAsync());

		var newBuilder = new NpgsqlConnectionStringBuilder(PostgresConnectionString)
			{ Username = info2.Username, Password = password2 };
		await using var newConnection = new NpgsqlConnection(newBuilder.ConnectionString);
		await newConnection.OpenAsync();
	}

	[Fact]
	public async Task EnsureAsync_DoesNotThrow_WhenRoleWasAlreadyCreatedConcurrently()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await EnsureActiveAdminCmkAsync(db);

		// roleExists 확인과 CREATE ROLE 사이의 경쟁으로 다른 인스턴스가 먼저 만들었을 때를 흉내낸다.
		await using (var conn = new NpgsqlConnection(PostgresConnectionString))
		{
			await conn.OpenAsync();
			try
			{
				await using var createCmd = new NpgsqlCommand("CREATE ROLE \"s3envmanager_backup_readonly\" LOGIN", conn);
				await createCmd.ExecuteNonQueryAsync();
			}
			catch (PostgresException ex) when (ex.SqlState == "42710")
			{
				// 이미 있으면 그대로 진행 - 검증하려는 상황(이미 존재하는 역할) 자체다.
			}
		}

		var service = new DbBackupAccountService(
			CreateDbContext(), CreateSecretKeyCipher(kms), new AuditLogger(CreateDbContext()));
		await service.EnsureAsync();

		var info = await service.GetCurrentAsync();
		Assert.NotNull(info);
	}

	[Fact]
	public async Task EnsureAsync_DoesNotRotate_WhenCredentialAlreadyExists()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await EnsureActiveAdminCmkAsync(db);

		var service = new DbBackupAccountService(
			CreateDbContext(), CreateSecretKeyCipher(kms), new AuditLogger(CreateDbContext()));
		await service.EnsureAsync();
		var info1 = await service.GetCurrentAsync();
		var password1 = await service.RevealCurrentPasswordAsync();

		// 재기동을 흉내낸다 - pg_dump 자동화가 참조하는 비밀번호가 예고 없이 stale해지면 안 된다.
		await service.EnsureAsync();
		var info2 = await service.GetCurrentAsync();
		var password2 = await service.RevealCurrentPasswordAsync();

		Assert.Equal(info1!.RotatedAt, info2!.RotatedAt);
		Assert.Equal(password1, password2);
	}

	[Fact]
	public async Task RevealCurrentPasswordAsync_LogsWhoRevealedIt()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await EnsureActiveAdminCmkAsync(db);

		var service = new DbBackupAccountService(
			CreateDbContext(), CreateSecretKeyCipher(kms), new AuditLogger(CreateDbContext()));
		await service.EnsureAsync();
		var info = await service.GetCurrentAsync();

		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];
		await service.RevealCurrentPasswordAsync(actorUserId);

		await using var verifyDb = CreateDbContext();
		var revealLog = await verifyDb.AuditLogs.SingleAsync(a =>
			a.EventType == AuditEventTypes.DbBackupAccountPasswordRevealed && a.ActorUserId == actorUserId);
		Assert.Contains(info!.Username, revealLog.Details, StringComparison.Ordinal);
		Assert.DoesNotContain("password", revealLog.Details, StringComparison.OrdinalIgnoreCase);
	}

	private static async Task EnsureActiveAdminCmkAsync(ApplicationDbContext db)
	{
		var hasActive = await db.CmkRegistrations.AsNoTracking()
			.AnyAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active);
		if (hasActive)
		{
			return;
		}

		var arn = $"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = arn,
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);

	private static IAppSecretKeyCipher CreateSecretKeyCipher(FakeKmsKeyOperations kms) =>
		new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache());
}

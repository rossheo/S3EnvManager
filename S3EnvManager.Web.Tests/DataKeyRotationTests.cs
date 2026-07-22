using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>"최신 세대"는 CmkId로 스코프하지 않고 테이블 전체에서 CreatedAt이 가장 최근인
/// 한 행이므로(AppSecretKeyCipher와 동일 설계), 다른 테스트가 남긴 세대와 섞이지 않도록
/// 시작 시 테이블을 비운다(직렬 실행이라 안전).</summary>
public class DataKeyRotationTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task RotateIfDueAsync_CreatesNewGeneration_WhenIntervalHasElapsed()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var auditLogger = new AuditLogger(CreateDbContext());

		var adminCmk = await RegisterAndPromoteDedicatedAdminCmkAsync(kms);
		await SetIntervalDaysAsync(db, days: 14);
		await ClearAllGenerationsAsync(db);

		var oldGeneration = new DataKeyGeneration
		{
			KeyId = Guid.NewGuid(),
			CiphertextBlob =
				(await kms.GenerateDataKeyAsync(adminCmk.Arn, new Dictionary<string, string>())).CiphertextBlob,
			CmkId = adminCmk.CmkId,
			CreatedAt = DateTimeOffset.UtcNow.AddDays(-15),
		};
		db.DataKeyGenerations.Add(oldGeneration);
		await db.SaveChangesAsync();

		await DataKeyRotationService.RotateIfDueAsync(CreateDbContext(), kms, auditLogger, TimeProvider.System);

		await using var verifyDb = CreateDbContext();
		var generations = await verifyDb.DataKeyGenerations.AsNoTracking()
			.Where(g => g.CmkId == adminCmk.CmkId)
			.OrderByDescending(g => g.CreatedAt)
			.ToListAsync();
		Assert.True(generations.Count >= 2);
		Assert.NotEqual(oldGeneration.KeyId, generations[0].KeyId);

		var log = await verifyDb.AuditLogs.SingleAsync(a =>
			a.EventType == AuditEventTypes.DataKeyRotated && a.Details!.Contains(generations[0].KeyId.ToString()));
		Assert.Contains(adminCmk.Arn, log.Details);
	}

	[Fact]
	public async Task RotateIfDueAsync_DoesNothing_WhenIntervalHasNotElapsed()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var auditLogger = new AuditLogger(CreateDbContext());

		var adminCmk = await RegisterAndPromoteDedicatedAdminCmkAsync(kms);
		await SetIntervalDaysAsync(db, days: 14);
		await ClearAllGenerationsAsync(db);

		var recentGeneration = new DataKeyGeneration
		{
			KeyId = Guid.NewGuid(),
			CiphertextBlob =
				(await kms.GenerateDataKeyAsync(adminCmk.Arn, new Dictionary<string, string>())).CiphertextBlob,
			CmkId = adminCmk.CmkId,
			CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
		};
		db.DataKeyGenerations.Add(recentGeneration);
		await db.SaveChangesAsync();

		await DataKeyRotationService.RotateIfDueAsync(CreateDbContext(), kms, auditLogger, TimeProvider.System);

		await using var verifyDb = CreateDbContext();
		var generations = await verifyDb.DataKeyGenerations.AsNoTracking()
			.Where(g => g.CmkId == adminCmk.CmkId)
			.ToListAsync();
		Assert.Single(generations);
	}

	[Fact]
	public async Task RotateNowAsync_CreatesNewGeneration_EvenWhenIntervalHasNotElapsed()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var auditLogger = new AuditLogger(CreateDbContext());

		var adminCmk = await RegisterAndPromoteDedicatedAdminCmkAsync(kms);
		await SetIntervalDaysAsync(db, days: 14);
		await ClearAllGenerationsAsync(db);

		var recentGeneration = new DataKeyGeneration
		{
			KeyId = Guid.NewGuid(),
			CiphertextBlob =
				(await kms.GenerateDataKeyAsync(adminCmk.Arn, new Dictionary<string, string>())).CiphertextBlob,
			CmkId = adminCmk.CmkId,
			CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
		};
		db.DataKeyGenerations.Add(recentGeneration);
		await db.SaveChangesAsync();

		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];
		var outcome = await DataKeyRotationService.RotateNowAsync(
			CreateDbContext(), kms, auditLogger, TimeProvider.System, actorUserId);
		Assert.Equal(DataKeyRotationOutcome.Rotated, outcome);

		await using var verifyDb = CreateDbContext();
		var generations = await verifyDb.DataKeyGenerations.AsNoTracking()
			.Where(g => g.CmkId == adminCmk.CmkId)
			.OrderByDescending(g => g.CreatedAt)
			.ToListAsync();
		Assert.True(generations.Count >= 2);
		Assert.NotEqual(recentGeneration.KeyId, generations[0].KeyId);

		var log = await verifyDb.AuditLogs.SingleAsync(a =>
			a.EventType == AuditEventTypes.DataKeyRotated && a.Details!.Contains(generations[0].KeyId.ToString()));
		Assert.Equal(actorUserId, log.ActorUserId);
	}

	[Fact]
	public async Task RotateNowAsync_ReturnsNoGenerationsYet_WhenNoGenerationExists()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var auditLogger = new AuditLogger(CreateDbContext());

		await RegisterAndPromoteDedicatedAdminCmkAsync(kms);
		await SetIntervalDaysAsync(db, days: 14);
		await ClearAllGenerationsAsync(db);

		var outcome = await DataKeyRotationService.RotateNowAsync(
			CreateDbContext(), kms, auditLogger, TimeProvider.System, actorUserId: null);
		Assert.Equal(DataKeyRotationOutcome.NoGenerationsYet, outcome);
	}

	[Fact]
	public async Task SetIntervalDaysAsync_ValidatesRangeAndAudits()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var settingsService = new DataKeyRotationSettingsService(
			CreateDbContext(), new AuditLogger(CreateDbContext()));
		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => settingsService.SetIntervalDaysAsync(0, actorUserId));
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => settingsService.SetIntervalDaysAsync(3651, actorUserId));

		await settingsService.SetIntervalDaysAsync(30, actorUserId);
		Assert.Equal(30, await settingsService.GetIntervalDaysAsync());

		await using var verifyDb = CreateDbContext();
		var log = await verifyDb.AuditLogs
			.Where(a => a.EventType == AuditEventTypes.DataKeyRotationIntervalChanged && a.ActorUserId == actorUserId)
			.OrderByDescending(a => a.OccurredAt).FirstAsync();
		Assert.Contains("\"newDays\":30", log.Details);
	}

	private static async Task ClearAllGenerationsAsync(ApplicationDbContext db)
	{
		// FK Restrict 제약 순서상 참조하는 테이블부터 지운다(직렬 실행이라 안전).
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DbBackupAccountCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataKeyGenerations\"");
	}

	private static async Task<CmkRegistration> RegisterAndPromoteDedicatedAdminCmkAsync(FakeKmsKeyOperations kms)
	{
		var arn = $"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), kms, new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());
		var registration = await registryService.RegisterAsync(CmkRole.Admin, arn);
		if (registration.Status != CmkStatus.Active)
		{
			await registryService.PromoteAsync(registration.CmkId);
		}
		return registration;
	}

	private static async Task SetIntervalDaysAsync(ApplicationDbContext db, Int32 days)
	{
		var settings = await db.DataKeyRotationSettings.SingleOrDefaultAsync(
			s => s.Id == Database.Models.DataKeyRotationSettings.SingletonId);
		if (settings is null)
		{
			db.DataKeyRotationSettings.Add(new Database.Models.DataKeyRotationSettings
			{
				RotationIntervalDays = days,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}
		else
		{
			settings.RotationIntervalDays = days;
		}
		await db.SaveChangesAsync();
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>App 삭제/폐기를 실 Postgres + 인메모리 fake AWS(S3/KMS/IAM)로 검증한다.</summary>
public class AppDeletionAndPurgeTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task DeleteAsync_RevokesCredentialsAndDeletesIamUser_AndIsIdempotent()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "del-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var iam = new FakeAppCredentialProvisioner();
		var adminArn = await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
		await GetOrCreateActiveCmkAsync(db, CmkRole.App);

		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);
		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		foreach (var n in new[] { EnvName.Dev, EnvName.Staging, EnvName.Product })
		{
			app.Envs.Add(new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = n });
		}
		db.Apps.Add(app);
		await db.SaveChangesAsync();

		var credentialService = new AppCredentialService(
			CreateDbContext(), iam, new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()), new PrimaryStorageSettingsStore(CreateDbContext()));
		var (credential, _) = await credentialService.IssueAsync(app.Id);
		Assert.True(iam.Users[appName].Exists);

		var deletionService = new AppDeletionService(CreateDbContext(), iam, new AuditLogger(CreateDbContext()));
		await deletionService.DeleteAsync(app.Id);

		await using (var verifyDb = CreateDbContext())
		{
			var reloadedApp = await verifyDb.Apps.SingleAsync(a => a.Id == app.Id);
			Assert.NotNull(reloadedApp.DeletedAt);
			Assert.Null(reloadedApp.PurgedAt);

			var reloadedCredential = await verifyDb.AppCredentials.SingleAsync(c => c.Id == credential.Id);
			Assert.NotNull(reloadedCredential.RevokedAt);
		}

		Assert.False(iam.Users.ContainsKey(appName));

		await deletionService.DeleteAsync(app.Id);
	}

	// 자격증명이 하나도 없는 App을 지우면 CredentialRevoked가 한 건도 안 남는다 - 그래도 삭제
	// 자체는 감사 로그에 나타나야 한다(그 전에는 흔적이 아예 없었다).
	[Fact]
	public async Task DeleteAsync_LogsAppDeleted_EvenWhenNoActiveCredentials()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "del-noc-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var iam = new FakeAppCredentialProvisioner();

		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		db.Apps.Add(app);
		await db.SaveChangesAsync();

		var actorUserId = "actor-" + Guid.NewGuid().ToString("N")[..8];
		var deletionService = new AppDeletionService(
			CreateDbContext(), iam, new AuditLogger(CreateDbContext()));
		await deletionService.DeleteAsync(app.Id, actorUserId);

		await using var verifyDb = CreateDbContext();
		var logs = await verifyDb.AuditLogs.AsNoTracking().Where(l => l.AppId == app.Id).ToListAsync();
		Assert.DoesNotContain(logs, l => l.EventType == AuditEventTypes.CredentialRevoked);

		var deletedLog = Assert.Single(logs, l => l.EventType == AuditEventTypes.AppDeleted);
		Assert.Equal(actorUserId, deletedLog.ActorUserId);
		Assert.Contains(appName, deletedLog.Details);
	}

	[Fact]
	public async Task PurgeEligibleAppsAsync_DeletesObjects_OnlyAfterRetentionPeriod_AndMarksPurgedAt()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "purge-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
		await GetOrCreateActiveCmkAsync(db, CmkRole.App);

		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);
		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		var devEnv = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(devEnv);
		db.Apps.Add(app);
		await db.SaveChangesAsync();

		var store = new FakeSecretObjectStore();
		var bundleService = new SecretBundleService(
			CreateDbContext(), store, kms, kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()));
		await bundleService.SaveAsync(
			devEnv.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "1" });
		await bundleService.SaveAsync(devEnv.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["A"] = "override" }, kind: SecretBundleKind.Overwrite);

		var objectKey = $"{appName}/dev.env";
		var overwriteObjectKey = $"{appName}/dev.overwrite.env";
		Assert.NotNull(await store.GetCurrentAsync(TestBucket, objectKey));
		Assert.NotNull(await store.GetCurrentAsync(TestBucket, overwriteObjectKey));

		app.DeletedAt = DateTimeOffset.UtcNow.AddDays(-1);
		await db.SaveChangesAsync();
		await AppPurgeService.PurgeEligibleAppsAsync(
			CreateDbContext(), store, new PrimaryStorageSettingsStore(CreateDbContext()),
			new AuditLogger(CreateDbContext()), TimeProvider.System);
		Assert.NotNull(await store.GetCurrentAsync(TestBucket, objectKey));
		await using (var verifyDb1 = CreateDbContext())
		{
			Assert.Null((await verifyDb1.Apps.SingleAsync(a => a.Id == app.Id)).PurgedAt);
		}

		app.DeletedAt = DateTimeOffset.UtcNow.AddDays(-61);
		await db.SaveChangesAsync();
		await AppPurgeService.PurgeEligibleAppsAsync(
			CreateDbContext(), store, new PrimaryStorageSettingsStore(CreateDbContext()),
			new AuditLogger(CreateDbContext()), TimeProvider.System);
		Assert.Null(await store.GetCurrentAsync(TestBucket, objectKey));
		Assert.Null(await store.GetCurrentAsync(TestBucket, overwriteObjectKey));
		await using (var verifyDb2 = CreateDbContext())
		{
			Assert.NotNull((await verifyDb2.Apps.SingleAsync(a => a.Id == app.Id)).PurgedAt);

			// 보존 기간 전 호출에서는 아무것도 안 지웠으므로 AppPurged도 그때는 남지 않아야 한다 -
			// 실제로 지운 두 번째 호출에서만 정확히 한 건.
			var purgeLog = Assert.Single(await verifyDb2.AuditLogs.AsNoTracking()
				.Where(l => l.AppId == app.Id && l.EventType == AuditEventTypes.AppPurged).ToListAsync());
			Assert.Null(purgeLog.ActorUserId);
			Assert.Contains(appName, purgeLog.Details);
		}
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static async Task<string> GetOrCreateActiveCmkAsync(ApplicationDbContext db, CmkRole role)
	{
		var existing = await db.CmkRegistrations.AsNoTracking()
			.Where(c => c.Role == role && c.Status == CmkStatus.Active)
			.Select(c => c.Arn)
			.FirstOrDefaultAsync();
		if (existing is not null)
		{
			return existing;
		}

		var arn = $"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = arn,
			Role = role,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
		return arn;
	}

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

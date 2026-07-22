using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>감사 로그를 실 Postgres + 인메모리 fake AWS(S3/KMS/IAM)로 검증한다.</summary>
public class AuditLoggingTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task SecretEdit_LogsOnlyKeyNames_NeverActualValues()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "audit-edit-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
		await GetOrCreateActiveCmkAsync(db, CmkRole.App);

		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		var devEnv = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(devEnv);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);

		var bundleService = new SecretBundleService(
			CreateDbContext(), new FakeSecretObjectStore(), kms, kms,
			new AuditLogger(CreateDbContext()), new PrimaryStorageSettingsStore(CreateDbContext()),
			new MemoryCache(new MemoryCacheOptions()));
		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];

		const string secretValue = "super-secret-value-should-never-appear-in-log";
		var outcome = await bundleService.SaveAsync(
			devEnv.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["FOO"] = secretValue }, actorUserId);
		Assert.IsType<SaveSuccess>(outcome);

		await using var verifyDb = CreateDbContext();
		var log = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.SecretEdited && a.AppId == app.Id);
		Assert.Equal(actorUserId, log.ActorUserId);
		Assert.Contains("FOO", log.Details);
		Assert.DoesNotContain(secretValue, log.Details);

		var session = await bundleService.LoadForEditAsync(devEnv.Id);
		var updated = new Dictionary<string, string>(session.Values)
			{ ["FOO"] = "changed-" + secretValue, ["BAR"] = "new-value" };
		var outcome2 = await bundleService.SaveAsync(
			devEnv.Id, session.Values, session.BaseETag, updated, actorUserId);
		Assert.IsType<SaveSuccess>(outcome2);

		var logs = await verifyDb.AuditLogs
			.Where(a => a.EventType == AuditEventTypes.SecretEdited && a.AppId == app.Id)
			.OrderBy(a => a.OccurredAt).ToListAsync();
		var secondLog = logs[1];
		Assert.Contains("\"changed\":[\"FOO\"]", secondLog.Details);
		Assert.Contains("\"added\":[\"BAR\"]", secondLog.Details);
		Assert.DoesNotContain(secretValue, secondLog.Details);
	}

	[Fact]
	public async Task CredentialIssueAndRevoke_AreLogged()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "audit-cred-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
		await GetOrCreateActiveCmkAsync(db, CmkRole.App);

		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);

		var credentialService = new AppCredentialService(
			CreateDbContext(), new FakeAppCredentialProvisioner(),
			new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()), new PrimaryStorageSettingsStore(CreateDbContext()));
		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];

		var (credential, _) = await credentialService.IssueAsync(app.Id, actorUserId);
		await credentialService.RevokeAsync(credential.Id, actorUserId);

		await using var verifyDb = CreateDbContext();
		var issuedLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CredentialIssued && a.AppId == app.Id);
		Assert.Equal(actorUserId, issuedLog.ActorUserId);
		Assert.Contains(credential.AccessKeyId, issuedLog.Details);

		var revokedLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CredentialRevoked && a.AppId == app.Id);
		Assert.Equal(actorUserId, revokedLog.ActorUserId);
		Assert.Contains(credential.AccessKeyId, revokedLog.Details);
	}

	[Fact]
	public async Task DeleteExpiredLogsAsync_RemovesOnlyLogsOlderThanRetentionPeriod()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var oldLog = new AuditLog
			{ Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow.AddDays(-181), EventType = "TestOld" };
		var recentLog = new AuditLog
			{ Id = Guid.NewGuid(), OccurredAt = DateTimeOffset.UtcNow.AddDays(-1), EventType = "TestRecent" };
		db.AuditLogs.AddRange(oldLog, recentLog);
		await db.SaveChangesAsync();

		await AuditLogRetentionService.DeleteExpiredLogsAsync(CreateDbContext(), TimeProvider.System);

		await using var verifyDb = CreateDbContext();
		Assert.False(await verifyDb.AuditLogs.AnyAsync(a => a.Id == oldLog.Id));
		Assert.True(await verifyDb.AuditLogs.AnyAsync(a => a.Id == recentLog.Id));
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

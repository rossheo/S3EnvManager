using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>S3EnvManagerConfigurationProvider는 항상 실 AmazonS3Client를 직접 생성해(주입
/// 지점 없음) fake로 대체할 수 없어, 실제 3단 우선순위 동작 검증은 이식하지 않았다.</summary>
public class OverwritePriorityTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task BaseAndOverwrite_AreIndependentObjects_WithSeparateETagTracking()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var appName = "overwrite-etag-" + Guid.NewGuid().ToString("N")[..8];
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

		var baseOutcome = await bundleService.SaveAsync(
			devEnv.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["A"] = "base-value" });
		Assert.IsType<SaveSuccess>(baseOutcome);

		var overwriteOutcome = await bundleService.SaveAsync(
			devEnv.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["A"] = "overwrite-value" },
			kind: SecretBundleKind.Overwrite);
		Assert.IsType<SaveSuccess>(overwriteOutcome);

		var baseSession = await bundleService.LoadForEditAsync(devEnv.Id, SecretBundleKind.Base);
		var overwriteSession = await bundleService.LoadForEditAsync(devEnv.Id, SecretBundleKind.Overwrite);
		Assert.Equal("base-value", baseSession.Values["A"]);
		Assert.Equal("overwrite-value", overwriteSession.Values["A"]);

		var secondBaseOutcome = await bundleService.SaveAsync(
			devEnv.Id, baseSession.Values, baseSession.BaseETag,
			new Dictionary<string, string> { ["A"] = "base-value-2" });
		Assert.IsType<SaveSuccess>(secondBaseOutcome);
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

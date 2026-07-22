using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>실 IAM 인증/권한 경계 검증은 fake로는 의미가 없어 제외했다(실AWS 필요).</summary>
public class AppCredentialServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task IssueAsync_ReturnsWorkingCredential_ThatCanBeRevealedAgain()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var appName = "cred-" + Guid.NewGuid().ToString("N")[..8];
		var (app, env) = await fixture.RegisterAppAsync(appName);

		var bundleService = fixture.CreateSecretBundleService();
		var secretValues = new Dictionary<string, string> { ["FOO"] = "bar" };
		var saveOutcome = await bundleService.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, secretValues);
		Assert.IsType<SaveSuccess>(saveOutcome);

		var credentialService = fixture.CreateCredentialService();
		var (credential, secretAccessKey) = await credentialService.IssueAsync(app.Id);
		Assert.False(string.IsNullOrWhiteSpace(credential.AccessKeyId));
		Assert.False(string.IsNullOrWhiteSpace(secretAccessKey));

		var revealed = await credentialService.RevealAsync(credential.Id);
		Assert.Equal(secretAccessKey, revealed);
	}

	[Fact]
	public async Task RevokeAsync_MarksCredentialRevoked_AndDeletesTheAccessKey()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var appName = "revoke-" + Guid.NewGuid().ToString("N")[..8];
		var (app, _) = await fixture.RegisterAppAsync(appName);

		var credentialService = fixture.CreateCredentialService();
		var (credential, _) = await credentialService.IssueAsync(app.Id);
		Assert.Contains(credential.AccessKeyId, fixture.Provisioner.Users[appName].AccessKeyIds);

		await credentialService.RevokeAsync(credential.Id);

		Assert.DoesNotContain(credential.AccessKeyId, fixture.Provisioner.Users[appName].AccessKeyIds);

		var list = await credentialService.ListAsync(app.Id);
		var stored = Assert.Single(list);
		Assert.NotNull(stored.RevokedAt);
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private sealed class Fixture
	{
		public FakeAppCredentialProvisioner Provisioner { get; } = new();
		public FakeKmsKeyOperations Kms { get; } = new();

		public static async Task<Fixture> CreateAsync()
		{
			var fixture = new Fixture();
			await using var db = CreateDbContext();
			await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
			await GetOrCreateActiveCmkAsync(db, CmkRole.App);
			await new PrimaryStorageSettingsStore(db).SaveAsync(null, TestBucket);
			return fixture;
		}

		public async Task<(App App, Env Env)> RegisterAppAsync(string appName)
		{
			await using var db = CreateDbContext();
			var app = new App
			{
				Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
			};
			var envs = new[] { EnvName.Dev, EnvName.Staging, EnvName.Product }
				.Select(n => new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = n });
			foreach (var e in envs)
			{
				app.Envs.Add(e);
			}
			db.Apps.Add(app);
			await db.SaveChangesAsync();
			return (app, app.Envs.Single(e => e.Name == EnvName.Dev));
		}

		public SecretBundleService CreateSecretBundleService() =>
			new(
				CreateDbContext(), new FakeSecretObjectStore(), Kms, Kms,
				new AuditLogger(CreateDbContext()), new PrimaryStorageSettingsStore(CreateDbContext()),
				new MemoryCache(new MemoryCacheOptions()));

		public AppCredentialService CreateCredentialService()
		{
			var db = CreateDbContext();
			var cache = new DataKeyCache();
			return new AppCredentialService(
				db, Provisioner, new AppSecretKeyCipher(db, Kms, cache), new AuditLogger(db),
				new PrimaryStorageSettingsStore(CreateDbContext()));
		}

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
}

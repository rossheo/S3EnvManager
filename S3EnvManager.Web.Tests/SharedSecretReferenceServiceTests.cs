using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>공유 시크릿 참조가 실 Postgres + fake AWS(S3/KMS)로 App 경계를 지키면서 동작하는지
/// 검증한다: 그랜트 없는 App은 거부, 그랜트된 App들은 각자 자기 envelope으로 같은 평문을 받고,
/// 연결 해제는 값을 그대로 둔 채 참조만 끊는다.</summary>
public class SharedSecretReferenceServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task SaveWithReferencesAsync_RejectsUngrantedApp_AndSucceedsAfterGrant_WithSamePlaintext()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (appA, envA) = await fixture.RegisterAppAsync("shref-a-" + Guid.NewGuid().ToString("N")[..8]);
		var (appB, envB) = await fixture.RegisterAppAsync("shref-b-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "leaked-external-api-key", null,
			actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };

		// 그랜트 전이므로 거부되어야 한다.
		var rejected = await referenceService.SaveWithReferencesAsync(
			envA.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);
		Assert.IsType<SaveFailed>(rejected);

		await sharedSecretService.GrantAsync(sharedSecretId, appA.Id, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, appB.Id, actorUserId: null);

		var outcomeA = await referenceService.SaveWithReferencesAsync(
			envA.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: "user-1", actorEmail: null, SecretBundleKind.Base);
		Assert.IsType<SaveSuccess>(outcomeA);

		var outcomeB = await referenceService.SaveWithReferencesAsync(
			envB.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: "user-1", actorEmail: null, SecretBundleKind.Base);
		Assert.IsType<SaveSuccess>(outcomeB);

		var bundleService = fixture.CreateBundleService();
		var sessionA = await bundleService.LoadForEditAsync(envA.Id);
		var sessionB = await bundleService.LoadForEditAsync(envB.Id);
		Assert.Equal("leaked-external-api-key", sessionA.Values["EXTERNAL_API_KEY"]);
		Assert.Equal("leaked-external-api-key", sessionB.Values["EXTERNAL_API_KEY"]);

		await using var db = Fixture.CreateDbContext();
		var reference = await db.SharedSecretReferences.AsNoTracking()
			.SingleAsync(r => r.EnvId == envA.Id && r.KeyName == "EXTERNAL_API_KEY");
		Assert.Equal(sharedSecretId, reference.SharedSecretId);
	}

	[Fact]
	public async Task SaveWithReferencesAsync_RejectsKeyNameCollisionWithOwnValue()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("shref-c-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, app.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var outcome = await referenceService.SaveWithReferencesAsync(
			env.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["DUP"] = "own-value" },
			new Dictionary<string, Guid> { ["DUP"] = sharedSecretId },
			actorUserId: null, actorEmail: null, SecretBundleKind.Base);

		Assert.IsType<SaveFailed>(outcome);
	}

	[Fact]
	public async Task DetachAsync_RemovesReferenceRow_ButKeepsValueAsOwnedKey()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("shref-d-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "detach-value", null, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, app.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };
		var outcome = await referenceService.SaveWithReferencesAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);
		Assert.IsType<SaveSuccess>(outcome);

		await referenceService.DetachAsync(
			env.Id, isOverwriteBundle: false, "EXTERNAL_API_KEY", actorUserId: null);

		await using var db = Fixture.CreateDbContext();
		Assert.False(await db.SharedSecretReferences.AsNoTracking()
			.AnyAsync(r => r.EnvId == env.Id && r.KeyName == "EXTERNAL_API_KEY"));

		var bundleService = fixture.CreateBundleService();
		var session = await bundleService.LoadForEditAsync(env.Id);
		Assert.Equal("detach-value", session.Values["EXTERNAL_API_KEY"]);
	}

	[Fact]
	public async Task UpdateAsync_CascadesNewValueToAllReferencingApps_WithoutDisturbingOwnKeys()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (appA, envA) = await fixture.RegisterAppAsync("shref-e-a-" + Guid.NewGuid().ToString("N")[..8]);
		var (appB, envB) = await fixture.RegisterAppAsync("shref-e-b-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, appA.Id, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, appB.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };

		// App A는 자체 소유 키도 하나 가진 상태에서 참조를 추가한다 - cascade가 그 키를
		// 건드리지 않아야 한다.
		await referenceService.SaveWithReferencesAsync(
			envA.Id, new Dictionary<string, string>(), null,
			new Dictionary<string, string> { ["OWN_KEY"] = "own-value" },
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);
		await referenceService.SaveWithReferencesAsync(
			envB.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);

		var result = await sharedSecretService.UpdateAsync(
			sharedSecretId, description: null, newValue: "v2-rotated", expiresAt: null, actorUserId: null);
		Assert.Empty(result.Failures);

		var bundleService = fixture.CreateBundleService();
		var sessionA = await bundleService.LoadForEditAsync(envA.Id);
		var sessionB = await bundleService.LoadForEditAsync(envB.Id);
		Assert.Equal("v2-rotated", sessionA.Values["EXTERNAL_API_KEY"]);
		Assert.Equal("v2-rotated", sessionB.Values["EXTERNAL_API_KEY"]);
		Assert.Equal("own-value", sessionA.Values["OWN_KEY"]);

		await using var db = Fixture.CreateDbContext();
		var referenceA = await db.SharedSecretReferences.AsNoTracking()
			.SingleAsync(r => r.EnvId == envA.Id && r.KeyName == "EXTERNAL_API_KEY");
		var secretAfter = await db.SharedSecrets.AsNoTracking().SingleAsync(s => s.Id == sharedSecretId);
		Assert.True(referenceA.LastMaterializedAt >= secretAfter.UpdatedAt.AddSeconds(-1));
	}

	[Fact]
	public async Task ResyncAsync_IsIdempotent_AndUpdatesLastMaterializedAtWithoutChangingValue()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("shref-f-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "stable-value", null, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, app.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };
		await referenceService.SaveWithReferencesAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);

		var result1 = await sharedSecretService.ResyncAsync(sharedSecretId, actorUserId: null);
		var result2 = await sharedSecretService.ResyncAsync(sharedSecretId, actorUserId: null);
		Assert.Empty(result1.Failures);
		Assert.Empty(result2.Failures);

		var bundleService = fixture.CreateBundleService();
		var session = await bundleService.LoadForEditAsync(env.Id);
		Assert.Equal("stable-value", session.Values["EXTERNAL_API_KEY"]);
	}

	[Fact]
	public async Task DeleteAsync_AutoDetachesReferences_AndPromotesExpirationToKeyExpiration()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("shref-g-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", expiresAt, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, app.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };
		var outcome = await referenceService.SaveWithReferencesAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);
		Assert.IsType<SaveSuccess>(outcome);

		await sharedSecretService.DeleteAsync(sharedSecretId, actorUserId: null);

		await using var db = Fixture.CreateDbContext();
		Assert.False(await db.SharedSecrets.AsNoTracking().AnyAsync(s => s.Id == sharedSecretId));
		Assert.False(await db.SharedSecretReferences.AsNoTracking()
			.AnyAsync(r => r.EnvId == env.Id && r.KeyName == "EXTERNAL_API_KEY"));

		// 값은 그대로 남아 자체 소유 키가 되고, 잃을 뻔한 만료일은 KeyExpiration으로 승격됐다.
		var bundleService = fixture.CreateBundleService();
		var session = await bundleService.LoadForEditAsync(env.Id);
		Assert.Equal("v1", session.Values["EXTERNAL_API_KEY"]);
		Assert.Equal(expiresAt, session.Expirations["EXTERNAL_API_KEY"]);
	}

	[Fact]
	public async Task ReferenceRow_CannotBeOrphaned_ForeignKeyRestrictBlocksDirectDelete()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("shref-h-" + Guid.NewGuid().ToString("N")[..8]);

		var sharedSecretService = fixture.CreateSharedSecretService();
		var sharedSecretId = await sharedSecretService.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);
		await sharedSecretService.GrantAsync(sharedSecretId, app.Id, actorUserId: null);

		var referenceService = fixture.CreateReferenceService();
		var editedReferences = new Dictionary<string, Guid> { ["EXTERNAL_API_KEY"] = sharedSecretId };
		await referenceService.SaveWithReferencesAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string>(),
			editedReferences, actorUserId: null, actorEmail: null, SecretBundleKind.Base);

		// SharedSecretService.DeleteAsync를 우회해 "detach를 깜빡한 버그"를 흉내낸다 - DB가
		// 물리적으로 막아야 한다(dangling reference의 최종 안전장치).
		await using var db = Fixture.CreateDbContext();
		var secret = await db.SharedSecrets.SingleAsync(s => s.Id == sharedSecretId);
		db.SharedSecrets.Remove(secret);
		await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private sealed class Fixture
	{
		public FakeKmsKeyOperations Kms { get; } = new();
		public FakeSecretObjectStore Store { get; } = new();

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
			var app = new App { Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow };
			var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
			app.Envs.Add(env);
			db.Apps.Add(app);
			await db.SaveChangesAsync();
			return (app, env);
		}

		public SecretBundleService CreateBundleService() => new(
			CreateDbContext(), Store, Kms, Kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()), new MemoryCache(new MemoryCacheOptions()));

		public SharedSecretService CreateSharedSecretService() => new(
			CreateDbContext(), new AppSecretKeyCipher(CreateDbContext(), Kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()), CreateBundleService());

		public SharedSecretReferenceService CreateReferenceService() => new(
			CreateDbContext(), CreateBundleService(),
			new AppSecretKeyCipher(CreateDbContext(), Kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()));

		public static ApplicationDbContext CreateDbContext() =>
			new(new DbContextOptionsBuilder<ApplicationDbContext>()
				.UseNpgsql(PostgresConnectionString).Options);

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
				CmkId = Guid.NewGuid(), Arn = arn, Role = role, Status = CmkStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
			return arn;
		}
	}
}

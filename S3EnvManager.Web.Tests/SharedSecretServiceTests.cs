using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class SharedSecretServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task CreateThenLoad_RoundTripsValue_AndDoesNotStorePlaintext()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveAdminCmkAsync(kms);
		var service = CreateService(kms);

		var name = "ext-api-" + Guid.NewGuid().ToString("N")[..8];
		var expiresAt = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
		var id = await service.CreateAsync(
			name, "설명", "super-secret-external-api-key", expiresAt, actorUserId: "user-1");

		var loaded = await service.LoadForEditAsync(id);
		Assert.Equal(name, loaded.Name);
		Assert.Equal("super-secret-external-api-key", loaded.Value);
		Assert.Equal(expiresAt, loaded.ExpiresAt);

		await using var db = CreateDbContext();
		var raw = await db.SharedSecrets.AsNoTracking().SingleAsync(s => s.Id == id);
		Assert.DoesNotContain(
			"super-secret-external-api-key",
			System.Text.Encoding.UTF8.GetString(raw.Ciphertext));

		var list = await service.ListAsync();
		Assert.Contains(list, s => s.Id == id);
	}

	[Fact]
	public async Task UpdateAsync_ChangesValueAndExpiration_WithoutTouchingDescriptionOnlyChanges()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveAdminCmkAsync(kms);
		var service = CreateService(kms);

		var id = await service.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], "old-desc", "v1", null, actorUserId: null);

		var newExpiresAt = new DateTimeOffset(2031, 6, 1, 0, 0, 0, TimeSpan.Zero);
		var result = await service.UpdateAsync(id, "new-desc", "v2", newExpiresAt, actorUserId: null);
		Assert.Empty(result.Failures);

		var loaded = await service.LoadForEditAsync(id);
		Assert.Equal("new-desc", loaded.Description);
		Assert.Equal("v2", loaded.Value);
		Assert.Equal(newExpiresAt, loaded.ExpiresAt);
	}

	[Fact]
	public async Task GrantAsync_ThenListGrantedAppIds_ReflectsGrant_AndRevokeRemovesIt()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveAdminCmkAsync(kms);
		var service = CreateService(kms);
		var (appA, _) = await RegisterAppAsync();

		var id = await service.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);

		await service.GrantAsync(id, appA.Id, actorUserId: "admin-1");
		var granted = await service.ListGrantedAppIdsAsync(id);
		Assert.Contains(appA.Id, granted);

		// 그랜트를 두 번 줘도 중복 행이 생기지 않아야 한다(유니크 인덱스 + 존재 체크).
		await service.GrantAsync(id, appA.Id, actorUserId: "admin-1");
		Assert.Single(await service.ListGrantedAppIdsAsync(id));

		await service.RevokeGrantAsync(id, appA.Id, actorUserId: "admin-1");
		Assert.DoesNotContain(appA.Id, await service.ListGrantedAppIdsAsync(id));
	}

	[Fact]
	public async Task RevokeGrantAsync_AutoDetachesExistingReferences_WithoutLeavingDanglingRow()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveAdminCmkAsync(kms);
		var service = CreateService(kms);
		var (appA, envA) = await RegisterAppAsync();

		var id = await service.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);
		await service.GrantAsync(id, appA.Id, actorUserId: null);

		// Phase 3(참조 추가 UI)가 아직 없으므로, 이미 참조가 있는 상태를 직접 만들어
		// RevokeGrantAsync가 대칭적으로 detach하는지 검증한다.
		await using (var db = CreateDbContext())
		{
			db.SharedSecretReferences.Add(new SharedSecretReference
			{
				Id = Guid.NewGuid(),
				SharedSecretId = id,
				EnvId = envA.Id,
				IsOverwriteBundle = false,
				KeyName = "EXTERNAL_API_KEY",
				LastMaterializedAt = DateTimeOffset.UtcNow,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
		}

		await service.RevokeGrantAsync(id, appA.Id, actorUserId: null);

		await using var verifyDb = CreateDbContext();
		Assert.False(await verifyDb.SharedSecretReferences.AsNoTracking()
			.AnyAsync(r => r.SharedSecretId == id && r.EnvId == envA.Id));
	}

	[Fact]
	public async Task DeleteAsync_RemovesRegistryEntry()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		await GetOrCreateActiveAdminCmkAsync(kms);
		var service = CreateService(kms);

		var id = await service.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "v1", null, actorUserId: null);
		await service.DeleteAsync(id, actorUserId: null);

		await using var db = CreateDbContext();
		Assert.False(await db.SharedSecrets.AsNoTracking().AnyAsync(s => s.Id == id));
	}

	// 회귀 방지: SharedSecret은 AppCredential.SecretAccessKey와 같은 DataKeyGeneration 세대를
	// 공유하므로, admin CMK를 제거(재래핑)해도 SharedSecret이 계속 복호화돼야 한다.
	[Fact]
	public async Task SharedSecret_RemainsDecryptable_AfterAdminCmkRemoval()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var kms = new FakeKmsKeyOperations();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), kms, new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());

		var adminArnA = NewFakeArn();
		await using (var db = CreateDbContext())
		{
			db.CmkRegistrations.Add(new CmkRegistration
			{
				CmkId = Guid.NewGuid(),
				Arn = adminArnA,
				Role = CmkRole.Admin,
				Status = CmkStatus.Active,
				CreatedAt = DateTimeOffset.UtcNow,
			});
			await db.SaveChangesAsync();
		}

		var service = CreateService(kms);
		var id = await service.CreateAsync(
			"ext-api-" + Guid.NewGuid().ToString("N")[..8], null, "leaked-api-key-v1", null,
			actorUserId: null);

		var adminArnB = NewFakeArn();
		var registrationB = await registryService.RegisterAsync(CmkRole.Admin, adminArnB);
		await registryService.PromoteAsync(registrationB.CmkId);

		await using (var db = CreateDbContext())
		{
			var oldRegistration = await db.CmkRegistrations
				.SingleAsync(c => c.Arn == adminArnA);
			await registryService.RemoveAsync(oldRegistration.CmkId);
		}

		var loaded = await service.LoadForEditAsync(id);
		Assert.Equal("leaked-api-key-v1", loaded.Value);
	}

	private static SharedSecretService CreateService(FakeKmsKeyOperations kms) =>
		new(CreateDbContext(), new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()));

	private static async Task<(App App, Env Env)> RegisterAppAsync()
	{
		await using var db = CreateDbContext();
		var app = new App
		{
			Id = Guid.NewGuid(),
			Name = "shared-secret-" + Guid.NewGuid().ToString("N")[..8],
			CreatedAt = DateTimeOffset.UtcNow,
		};
		var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(env);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		return (app, env);
	}

	private static async Task GetOrCreateActiveAdminCmkAsync(FakeKmsKeyOperations kms)
	{
		await using var db = CreateDbContext();
		var existing = await db.CmkRegistrations.AsNoTracking()
			.AnyAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active);
		if (existing)
		{
			return;
		}

		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = NewFakeArn(),
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
	}

	private static string NewFakeArn() =>
		$"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";

	private static async Task ResetSharedCmkStateAsync()
	{
		await using var db = CreateDbContext();
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DbBackupAccountCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecretReferences\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecretAppGrants\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"SharedSecrets\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataKeyGenerations\"");
		await db.CmkRegistrations.ExecuteDeleteAsync();
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

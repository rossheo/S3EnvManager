using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>회귀 방지: admin CMK를 승격해도 이전 CMK로 감싼 번들을 열 수 있어야 한다
/// (과거 "활성" ARN으로 복호화를 시도해 IncorrectKeyException이 났던 버그).</summary>
public class CmkRegistryServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task PromotingAdminCmk_DoesNotBreakDecryptionOfBundlesEncryptedUnderThePreviousActiveCmk()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		// "role당 활성 CMK 하나" 전제 - 이전 테스트의 잔여 Active 행을 치우지 않으면 SingleAsync 조회가 깨진다.
		await ResetSharedCmkStateAsync();

		var appName = "cmk-promote-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), kms, new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());

		var adminArnA = NewFakeArn();
		var appArn = NewFakeArn();
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = adminArnA,
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = appArn,
			Role = CmkRole.App,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();

		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		var devEnv = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(devEnv);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);

		var store = new FakeSecretObjectStore();
		var bundleService = new SecretBundleService(
			CreateDbContext(), store, kms, kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()), new MemoryCache(new MemoryCacheOptions()));

		var values = new Dictionary<string, string> { ["FOO"] = "bar-under-cmk-a" };
		var saveOutcome = await bundleService.SaveAsync(devEnv.Id, new Dictionary<string, string>(), null, values);
		Assert.IsType<SaveSuccess>(saveOutcome);

		var adminArnB = NewFakeArn();
		var registrationB = await registryService.RegisterAsync(CmkRole.Admin, adminArnB);
		Assert.Equal(CmkStatus.Secondary, registrationB.Status);
		await registryService.PromoteAsync(registrationB.CmkId);

		await using var verifyDb = CreateDbContext();
		var activeAdmin = await verifyDb.CmkRegistrations.AsNoTracking()
			.SingleAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active);
		Assert.Equal(adminArnB, activeAdmin.Arn);
		var demotedA = await verifyDb.CmkRegistrations.AsNoTracking().SingleAsync(c => c.Arn == adminArnA);
		Assert.Equal(CmkStatus.Secondary, demotedA.Status);

		var reloaded = await bundleService.LoadForEditAsync(devEnv.Id);
		Assert.Equal(values, reloaded.Values);

		var newValues = new Dictionary<string, string> { ["FOO"] = "bar-under-cmk-b" };
		var secondSave = await bundleService.SaveAsync(devEnv.Id, reloaded.Values, reloaded.BaseETag, newValues);
		Assert.IsType<SaveSuccess>(secondSave);
		var reloadedAgain = await bundleService.LoadForEditAsync(devEnv.Id);
		Assert.Equal(newValues, reloadedAgain.Values);
	}

	[Fact]
	public async Task RegisteringAndPromotingAppCmk_ReappliesGrantToAlreadyIssuedCredentials()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var appName = "cmk-regrant-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var kms = new FakeKmsKeyOperations();
		var provisioner = new FakeAppCredentialProvisioner();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), provisioner,
			new FakeSecretObjectStore(), kms, new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());

		var adminArn = NewFakeArn();
		var appArnA = NewFakeArn();
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = adminArn,
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = appArnA,
			Role = CmkRole.App,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});

		var app = new App
		{
			Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
		};
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);

		var credentialService = new AppCredentialService(
			CreateDbContext(), provisioner,
			new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache()),
			new AuditLogger(CreateDbContext()), new PrimaryStorageSettingsStore(CreateDbContext()));
		await credentialService.IssueAsync(app.Id);

		var appArnB = NewFakeArn();
		var registrationB = await registryService.RegisterAsync(CmkRole.App, appArnB);

		var arnsAfterRegister = provisioner.Users[appName].AppFacingCmkArns;
		Assert.Contains(appArnA, arnsAfterRegister);
		Assert.Contains(appArnB, arnsAfterRegister);

		await registryService.PromoteAsync(registrationB.CmkId);
		var arnsAfterPromote = provisioner.Users[appName].AppFacingCmkArns;
		Assert.Contains(appArnA, arnsAfterPromote);
		Assert.Contains(appArnB, arnsAfterPromote);
	}

	[Fact]
	public async Task RemoveAsync_RewrapsBaseAndOverwriteBundles_ValuesUnchanged_AndDeregistersTheCmk()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var appName = "cmk-remove-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var provisioner = new FakeAppCredentialProvisioner();
		var store = new FakeSecretObjectStore();
		var kms = new FakeKmsKeyOperations();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), provisioner, store, kms,
			new FakeBootstrapAppIdentityProvisioner(), new PrimaryStorageSettingsStore(CreateDbContext()),
			new FakeKmsKeyAdministration());

		var adminArn = NewFakeArn();
		var appArnA = NewFakeArn();
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = adminArn,
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});
		var registrationA = new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = appArnA,
			Role = CmkRole.App,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		};
		db.CmkRegistrations.Add(registrationA);

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
			CreateDbContext(), store, kms, kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()), new MemoryCache(new MemoryCacheOptions()));

		// 재래핑 스윕이 base/overwrite 둘 다 커버하는지가 핵심(overwrite를 빠뜨리기 쉽다).
		var baseValues = new Dictionary<string, string> { ["FOO"] = "base-under-cmk-a" };
		var overwriteValues = new Dictionary<string, string> { ["FOO"] = "overwrite-under-cmk-a" };
		Assert.IsType<SaveSuccess>(
			await bundleService.SaveAsync(devEnv.Id, new Dictionary<string, string>(), null, baseValues));
		Assert.IsType<SaveSuccess>(await bundleService.SaveAsync(
			devEnv.Id, new Dictionary<string, string>(), null, overwriteValues, kind: SecretBundleKind.Overwrite));

		var appArnB = NewFakeArn();
		var registrationB = await registryService.RegisterAsync(CmkRole.App, appArnB);
		await registryService.PromoteAsync(registrationB.CmkId);

		await registryService.RemoveAsync(registrationA.CmkId);

		var baseStored = await store.GetCurrentAsync(TestBucket, $"{appName}/dev.env");
		var baseDocument = SopsDotEnvDocument.Parse(baseStored!.Content);
		Assert.Equal(appArnB, baseDocument.KmsEntries[1].Arn);

		var overwriteStored = await store.GetCurrentAsync(TestBucket, $"{appName}/dev.overwrite.env");
		var overwriteDocument = SopsDotEnvDocument.Parse(overwriteStored!.Content);
		Assert.Equal(appArnB, overwriteDocument.KmsEntries[1].Arn);

		var reloadedBase = await bundleService.LoadForEditAsync(devEnv.Id, SecretBundleKind.Base);
		Assert.Equal(baseValues, reloadedBase.Values);
		var reloadedOverwrite = await bundleService.LoadForEditAsync(devEnv.Id, SecretBundleKind.Overwrite);
		Assert.Equal(overwriteValues, reloadedOverwrite.Values);

		var appDecryptedBase = await SopsEnvelopeCodec.DecryptAsAppAsync(baseStored.Content, kms);
		Assert.Equal(baseValues, appDecryptedBase);
		var appDecryptedOverwrite = await SopsEnvelopeCodec.DecryptAsAppAsync(overwriteStored.Content, kms);
		Assert.Equal(overwriteValues, appDecryptedOverwrite);

		await using var verifyDb = CreateDbContext();
		Assert.False(await verifyDb.CmkRegistrations.AsNoTracking().AnyAsync(c => c.CmkId == registrationA.CmkId));
		var removedLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CmkRemoved && a.Details!.Contains(appArnA));
		Assert.Contains(appArnB, removedLog.Details);
	}

	[Fact]
	public async Task RemoveAsync_AdminRole_RewrapsCurrentVersion_DeletesDependentNoncurrentVersions_AndRewrapsDataKeyGenerations()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var appName = "cmk-remove-admin-" + Guid.NewGuid().ToString("N")[..8];
		await using var db = CreateDbContext();
		var provisioner = new FakeAppCredentialProvisioner();
		var store = new FakeSecretObjectStore();
		var kms = new FakeKmsKeyOperations();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), provisioner, store, kms,
			new FakeBootstrapAppIdentityProvisioner(), new PrimaryStorageSettingsStore(CreateDbContext()),
			new FakeKmsKeyAdministration());

		var adminArnA = NewFakeArn();
		var registrationA = new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = adminArnA,
			Role = CmkRole.Admin,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		};
		db.CmkRegistrations.Add(registrationA);
		var appArn = NewFakeArn();
		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = appArn,
			Role = CmkRole.App,
			Status = CmkStatus.Active,
			CreatedAt = DateTimeOffset.UtcNow,
		});

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
			CreateDbContext(), store, kms, kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()), new MemoryCache(new MemoryCacheOptions()));
		var objectKey = $"{appName}/dev.env";

		var v1 = new Dictionary<string, string> { ["FOO"] = "v1-under-cmk-a" };
		var v2 = new Dictionary<string, string> { ["FOO"] = "v2-under-cmk-a" };
		Assert.IsType<SaveSuccess>(
			await bundleService.SaveAsync(devEnv.Id, new Dictionary<string, string>(), null, v1));
		var session1 = await bundleService.LoadForEditAsync(devEnv.Id);
		Assert.IsType<SaveSuccess>(
			await bundleService.SaveAsync(devEnv.Id, session1.Values, session1.BaseETag, v2));

		var cache = new DataKeyCache();
		var secretKeyCipher = new AppSecretKeyCipher(CreateDbContext(), kms, cache);
		var credentialService = new AppCredentialService(
			CreateDbContext(), provisioner, secretKeyCipher, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()));
		var (credential, secretAccessKey) = await credentialService.IssueAsync(app.Id);
		var dataKeyIdBefore = credential.DataKeyId;
		await using (var verifyGenDb = CreateDbContext())
		{
			var generationBefore = await verifyGenDb.DataKeyGenerations.AsNoTracking()
				.SingleAsync(g => g.KeyId == dataKeyIdBefore);
			Assert.Equal(registrationA.CmkId, generationBefore.CmkId);
		}

		var adminArnB = NewFakeArn();
		var registrationB = await registryService.RegisterAsync(CmkRole.Admin, adminArnB);
		await registryService.PromoteAsync(registrationB.CmkId);

		var versionsBeforeRemoval = await store.ListVersionsAsync(TestBucket, objectKey);
		Assert.True(versionsBeforeRemoval.Count >= 2, "재래핑 검증을 위해 최소 2개 버전이 있어야 한다.");

		// 파괴적: CMK-A로 감싼 noncurrent 버전은 영구히 삭제된다.
		await registryService.RemoveAsync(registrationA.CmkId);

		var currentStored = await store.GetCurrentAsync(TestBucket, objectKey);
		var currentDocument = SopsDotEnvDocument.Parse(currentStored!.Content);
		Assert.Equal(adminArnB, currentDocument.KmsEntries[0].Arn);
		var currentValues = await SopsEnvelopeCodec.DecryptAsAdminAsync(currentStored.Content, kms);
		Assert.Equal(v2, currentValues);

		var versionsAfterRemoval = await store.ListVersionsAsync(TestBucket, objectKey);
		foreach (var version in versionsAfterRemoval)
		{
			var content = await store.GetVersionContentAsync(TestBucket, objectKey, version.VersionId);
			var document = SopsDotEnvDocument.Parse(content);
			Assert.NotEqual(adminArnA, document.KmsEntries[0].Arn);
		}

		await using (var verifyGenDb2 = CreateDbContext())
		{
			var generationAfter = await verifyGenDb2.DataKeyGenerations.AsNoTracking()
				.SingleAsync(g => g.KeyId == dataKeyIdBefore);
			Assert.Equal(registrationB.CmkId, generationAfter.CmkId);
		}
		var revealed = await new AppSecretKeyCipher(CreateDbContext(), kms, new DataKeyCache())
			.DecryptAsync(credential.EncryptedSecretKey, dataKeyIdBefore);
		Assert.Equal(secretAccessKey, revealed);

		await using var verifyDb = CreateDbContext();
		Assert.False(await verifyDb.CmkRegistrations.AsNoTracking().AnyAsync(c => c.CmkId == registrationA.CmkId));
		var removedLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CmkRemoved && a.Details!.Contains(adminArnA));
		Assert.Contains(adminArnB, removedLog.Details);
		Assert.Contains("deletedNoncurrentVersions", removedLog.Details);
	}

	[Fact]
	public async Task RemoveAsync_RejectsActiveCmk_ForBothRoles()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), new FakeKmsKeyOperations(), new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());

		var appArn = NewFakeArn();
		var appRegistration = await registryService.RegisterAsync(CmkRole.App, appArn);
		if (appRegistration.Status != CmkStatus.Active)
		{
			await registryService.PromoteAsync(appRegistration.CmkId);
		}
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => registryService.RemoveAsync(appRegistration.CmkId));

		var adminArn = NewFakeArn();
		var adminRegistration = await registryService.RegisterAsync(CmkRole.Admin, adminArn);
		if (adminRegistration.Status != CmkStatus.Active)
		{
			await registryService.PromoteAsync(adminRegistration.CmkId);
		}
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => registryService.RemoveAsync(adminRegistration.CmkId));
	}

	// 등록 시점에 managed 태그를 붙이지 않으면 콘솔에서 수동 생성한 키는 첫 실사용에서야
	// AccessDenied로 드러난다(admin 정책이 이 태그로 KMS 액션을 스코프하므로).
	[Fact]
	public async Task RegisterAsync_TagsTheKeyAsManaged_SoItCanActuallyBeUsedForEncryption()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var kmsAdmin = new FakeKmsKeyAdministration();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), new FakeKmsKeyOperations(), new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), kmsAdmin);
		var arn = NewFakeArn();

		await registryService.RegisterAsync(CmkRole.Admin, arn);

		Assert.Equal("true", kmsAdmin.Keys[arn].Tags[KmsAliasConventions.ManagedTagKey]);
	}

	[Fact]
	public async Task RegisterAsync_RejectsDuplicateArnWithinSameRole()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), new FakeKmsKeyOperations(), new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());
		var arn = NewFakeArn();

		await registryService.RegisterAsync(CmkRole.App, arn);
		await Assert.ThrowsAsync<InvalidOperationException>(() => registryService.RegisterAsync(CmkRole.App, arn));
	}

	[Fact]
	public async Task RegisterAsync_RejectsSameArnAcrossDifferentRoles()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), new FakeKmsKeyOperations(), new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());
		var arn = NewFakeArn();

		await registryService.RegisterAsync(CmkRole.Admin, arn);

		// 같은 CMK를 다른 role로 등록 허용하면 두 role의 복호화 권한 경계가 무너진다.
		await Assert.ThrowsAsync<InvalidOperationException>(() => registryService.RegisterAsync(CmkRole.App, arn));
	}

	[Fact]
	public async Task RegisterAndPromote_AreAudited()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedCmkStateAsync();

		await using var db = CreateDbContext();
		var registryService = new CmkRegistryService(
			CreateDbContext(), new AuditLogger(CreateDbContext()), new FakeAppCredentialProvisioner(),
			new FakeSecretObjectStore(), new FakeKmsKeyOperations(), new FakeBootstrapAppIdentityProvisioner(),
			new PrimaryStorageSettingsStore(CreateDbContext()), new FakeKmsKeyAdministration());
		var actorUserId = "user-" + Guid.NewGuid().ToString("N")[..8];

		// 첫 등록은 자동 Active라 PromoteAsync가 no-op이 된다 - 실제 승격을 만들려면 먼저 채워둬야 한다.
		var existingArn = NewFakeArn();
		await registryService.RegisterAsync(CmkRole.App, existingArn);

		var arn = NewFakeArn();
		var registration = await registryService.RegisterAsync(CmkRole.App, arn, actorUserId);
		Assert.Equal(CmkStatus.Secondary, registration.Status);
		await registryService.PromoteAsync(registration.CmkId, actorUserId);

		await using var verifyDb = CreateDbContext();
		var registeredLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CmkRegistered && a.Details!.Contains(arn));
		Assert.Equal(actorUserId, registeredLog.ActorUserId);

		var promotedLog = await verifyDb.AuditLogs
			.SingleAsync(a => a.EventType == AuditEventTypes.CmkPromoted && a.Details!.Contains(arn));
		Assert.Equal(actorUserId, promotedLog.ActorUserId);
	}

	private static string NewFakeArn() => $"arn:aws:kms:ap-northeast-2:000000000000:key/fake-{Guid.NewGuid():N}";

	private static async Task ResetSharedCmkStateAsync()
	{
		await using var db = CreateDbContext();
		// FK Restrict 제약 순서상 이 순서로 지워야 한다.
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DbBackupAccountCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataKeyGenerations\"");
		await db.CmkRegistrations.ExecuteDeleteAsync();
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

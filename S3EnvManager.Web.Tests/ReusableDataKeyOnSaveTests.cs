using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>감싼 데이터 키 재사용(FeatureSwitchKeys.ReuseDataKeyOnSave)을 검증한다.
///
/// KMS 호출이 줄었다는 것만으로는 부족하다 - 재사용한 데이터 키로 만든 번들이 **Application의
/// 읽기 경로(DecryptAsAppAsync)로도 열리는지**가 본질이다. 특히 App 경계를 넘어 재사용하면
/// 트레일러가 실제 wrap과 다른 encryption context를 주장해 그 번들이 영구히 복호화 불가능해지는데,
/// 저장은 성공하므로 조용한 데이터 손상이 된다.</summary>
public class ReusableDataKeyOnSaveTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task Reuse_WithinSameApp_SkipsKmsOnSecondSave_AndBothBundlesStayAppDecryptable()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		var store = new FakeSecretObjectStore();
		var appName = "reuse-same-" + Guid.NewGuid().ToString("N")[..8];
		var (app, baseEnv) = await SetupAppAsync(appName);
		var service = CreateService(store, kms, reuseEnabled: true);

		var kmsBefore = kms.TotalCalls;
		var first = new Dictionary<string, string> { ["A"] = "first" };
		Assert.IsType<SaveSuccess>(
			await service.SaveAsync(baseEnv.Id, new Dictionary<string, string>(), null, first));
		var afterFirst = kms.TotalCalls - kmsBefore;

		// 두 번째 저장은 같은 App/CMK 조합이라 감싼 키를 재사용한다.
		var second = new Dictionary<string, string> { ["A"] = "second" };
		Assert.IsType<SaveSuccess>(
			await service.SaveAsync(
				baseEnv.Id, new Dictionary<string, string>(), null, second,
				kind: SecretBundleKind.Overwrite));
		var afterSecond = kms.TotalCalls - kmsBefore - afterFirst;

		Assert.True(afterFirst > 0, "첫 저장은 데이터 키를 새로 만들어야 한다.");
		Assert.Equal(0, afterSecond);

		// 핵심: 두 번들 모두 Application 경로로 열려야 한다.
		Assert.Equal(first, await DecryptAsAppAsync(store, $"{appName}/dev.env", kms));
		Assert.Equal(second, await DecryptAsAppAsync(store, $"{appName}/dev.overwrite.env", kms));
	}

	// 캐시 키에서 appName이 빠지면 이 테스트가 잡는다 - App B의 번들이 App A의 context로 감싼
	// 키를 쓰게 되어 DecryptAsAppAsync가 영구히 실패한다.
	[Fact]
	public async Task Reuse_AcrossDifferentApps_DoesNotShareDataKey_AndBothStayAppDecryptable()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		var store = new FakeSecretObjectStore();
		var nameA = "reuse-a-" + Guid.NewGuid().ToString("N")[..8];
		var nameB = "reuse-b-" + Guid.NewGuid().ToString("N")[..8];
		var (_, envA) = await SetupAppAsync(nameA);
		var (_, envB) = await SetupAppAsync(nameB);
		var service = CreateService(store, kms, reuseEnabled: true);

		var kmsBefore = kms.TotalCalls;
		var valuesA = new Dictionary<string, string> { ["A"] = "app-a" };
		var valuesB = new Dictionary<string, string> { ["A"] = "app-b" };
		Assert.IsType<SaveSuccess>(
			await service.SaveAsync(envA.Id, new Dictionary<string, string>(), null, valuesA));
		Assert.IsType<SaveSuccess>(
			await service.SaveAsync(envB.Id, new Dictionary<string, string>(), null, valuesB));

		// App이 다르면 재사용하면 안 되므로 각각 새로 감싼다.
		Assert.True(kms.TotalCalls - kmsBefore >= 4, "App 경계를 넘어 데이터 키를 재사용했습니다.");

		Assert.Equal(valuesA, await DecryptAsAppAsync(store, $"{nameA}/dev.env", kms));
		Assert.Equal(valuesB, await DecryptAsAppAsync(store, $"{nameB}/dev.env", kms));
	}

	[Fact]
	public async Task Reuse_Disabled_MakesEverySaveGenerateItsOwnDataKey()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var kms = new FakeKmsKeyOperations();
		var store = new FakeSecretObjectStore();
		var appName = "reuse-off-" + Guid.NewGuid().ToString("N")[..8];
		var (_, env) = await SetupAppAsync(appName);
		var service = CreateService(store, kms, reuseEnabled: false);

		var kmsBefore = kms.TotalCalls;
		Assert.IsType<SaveSuccess>(await service.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "1" }));
		var afterFirst = kms.TotalCalls;
		Assert.IsType<SaveSuccess>(await service.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "2" },
			kind: SecretBundleKind.Overwrite));

		Assert.True(afterFirst - kmsBefore > 0);
		Assert.True(kms.TotalCalls - afterFirst > 0, "기본값(꺼짐)에서는 저장마다 새 데이터 키를 만들어야 한다.");
	}

	private static async Task<Dictionary<string, string>> DecryptAsAppAsync(
		FakeSecretObjectStore store, string objectKey, FakeKmsKeyOperations kms)
	{
		var stored = await store.GetCurrentAsync(TestBucket, objectKey);
		Assert.NotNull(stored);
		return await SopsEnvelopeCodec.DecryptAsAppAsync(stored!.Content, kms);
	}

	private static SecretBundleService CreateService(
		FakeSecretObjectStore store, FakeKmsKeyOperations kms, bool reuseEnabled) =>
		new(
			CreateDbContext(), store, kms, kms, new AuditLogger(CreateDbContext()),
			new PrimaryStorageSettingsStore(CreateDbContext()),
			new StubFeatureSwitchService(reuseEnabled),
			new ReusableDataKeyCache(TimeProvider.System));

	private static async Task<(App App, Env Env)> SetupAppAsync(string appName)
	{
		await using var db = CreateDbContext();
		await GetOrCreateActiveCmkAsync(db, CmkRole.Admin);
		await GetOrCreateActiveCmkAsync(db, CmkRole.App);
		await new PrimaryStorageSettingsStore(CreateDbContext()).SaveAsync(null, TestBucket);

		var app = new App { Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow };
		var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(env);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		return (app, env);
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

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);

	private sealed class StubFeatureSwitchService(bool enabled) : IFeatureSwitchService
	{
		public Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default) =>
			Task.FromResult(key == FeatureSwitchKeys.ReuseDataKeyOnSave && enabled);

		public Task<List<FeatureSwitchInfo>> ListAsync(CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task SetEnabledAsync(
			string key, bool enabled, string? actorUserId = null, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}
}

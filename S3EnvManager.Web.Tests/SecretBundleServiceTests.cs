using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class SecretBundleServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";
	private const string TestBucket = "fake-bucket";

	[Fact]
	public async Task HappyPath_SaveThenReload_ReturnsSameValues()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("happy-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var session = await service.LoadForEditAsync(env.Id);
		Assert.Null(session.BaseETag);
		Assert.Empty(session.Values);

		var values = new Dictionary<string, string> { ["FOO"] = "bar", ["DB_PASSWORD"] = "hunter2" };
		var outcome = await service.SaveAsync(env.Id, session.Values, session.BaseETag, values);
		var success = Assert.IsType<SaveSuccess>(outcome);
		Assert.NotNull(success.NewETag);

		var reloaded = await service.LoadForEditAsync(env.Id);
		Assert.Equal(values, reloaded.Values);
		Assert.Equal(success.NewETag, reloaded.BaseETag);
	}

	// 값마다 개별 암호화해 한 줄 ciphertext로 직렬화하므로 개행이 있는 값도 안전하게 왕복돼야 한다.
	[Fact]
	public async Task HappyPath_SaveThenReload_PreservesMultilineValue()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("multiline-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var pemLikeValue = "-----BEGIN CERTIFICATE-----\n" +
			"MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA...\n" +
			"-----END CERTIFICATE-----\n";
		var values = new Dictionary<string, string> { ["CERT_PEM"] = pemLikeValue, ["SHORT"] = "ok" };

		var session = await service.LoadForEditAsync(env.Id);
		var outcome = await service.SaveAsync(env.Id, session.Values, session.BaseETag, values);
		Assert.IsType<SaveSuccess>(outcome);

		var reloaded = await service.LoadForEditAsync(env.Id);
		Assert.Equal(pemLikeValue, reloaded.Values["CERT_PEM"]);
		Assert.Equal(values, reloaded.Values);
	}

	[Theory]
	[InlineData("FOO=BAR")]
	[InlineData("FOO\nsops_mac=tampered")]
	[InlineData("sops_mac")]
	public async Task SaveAsync_RejectsKeysThatWouldBreakSopsFormat_AndDoesNotTouchPrimaryStorage(
		string invalidKey)
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("badkey-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var values = new Dictionary<string, string> { [invalidKey] = "whatever" };
		var outcome = await service.SaveAsync(env.Id, new Dictionary<string, string>(), null, values);
		Assert.IsType<SaveFailed>(outcome);

		var session = await service.LoadForEditAsync(env.Id);
		Assert.Null(session.BaseETag);
		Assert.Empty(session.Values);
	}

	[Fact]
	public async Task Conflict_NonOverlappingKeys_AutoMergesWithoutRealConflict()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("merge-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var baseValues = new Dictionary<string, string> { ["A"] = "1", ["B"] = "1" };
		var initial = await service.SaveAsync(env.Id, new Dictionary<string, string>(), null, baseValues);
		Assert.IsType<SaveSuccess>(initial);

		var sessionA = await service.LoadForEditAsync(env.Id);
		var sessionB = await service.LoadForEditAsync(env.Id);

		var bValues = new Dictionary<string, string>(sessionB.Values) { ["B"] = "9" };
		var outcomeB = await service.SaveAsync(env.Id, sessionB.Values, sessionB.BaseETag, bValues);
		Assert.IsType<SaveSuccess>(outcomeB);

		var aValues = new Dictionary<string, string>(sessionA.Values) { ["A"] = "2" };
		var outcomeA = await service.SaveAsync(env.Id, sessionA.Values, sessionA.BaseETag, aValues);
		var conflict = Assert.IsType<SaveConflict>(outcomeA);
		Assert.Empty(conflict.RealConflicts);
		Assert.Contains("B", conflict.AutoAppliedTheirsKeys);
		Assert.Equal("2", conflict.MergedValues["A"]);
		Assert.Equal("9", conflict.MergedValues["B"]);

		var retryOutcome = await service.SaveAsync(
			env.Id, conflict.TheirsSnapshot, conflict.TheirsETag, conflict.MergedValues);
		var success = Assert.IsType<SaveSuccess>(retryOutcome);

		var final = await service.LoadForEditAsync(env.Id);
		Assert.Equal("2", final.Values["A"]);
		Assert.Equal("9", final.Values["B"]);
		Assert.Equal(success.NewETag, final.BaseETag);
	}

	[Fact]
	public async Task Conflict_SameKeyDifferentValues_ReturnsRealConflictForUserToResolve()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("realconflict-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var initial = await service.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "1" });
		Assert.IsType<SaveSuccess>(initial);

		var sessionA = await service.LoadForEditAsync(env.Id);
		var sessionB = await service.LoadForEditAsync(env.Id);

		var outcomeB = await service.SaveAsync(env.Id, sessionB.Values, sessionB.BaseETag,
			new Dictionary<string, string>(sessionB.Values) { ["A"] = "from-b" });
		Assert.IsType<SaveSuccess>(outcomeB);

		var outcomeA = await service.SaveAsync(env.Id, sessionA.Values, sessionA.BaseETag,
			new Dictionary<string, string>(sessionA.Values) { ["A"] = "from-a" });
		var conflict = Assert.IsType<SaveConflict>(outcomeA);

		var realConflict = Assert.Single(conflict.RealConflicts);
		Assert.Equal("A", realConflict.Key);
		Assert.Equal("from-a", realConflict.Mine);
		Assert.Equal("from-b", realConflict.Theirs);
	}

	[Fact]
	public async Task GetKeyCountAsync_ReflectsSavedEntryCount_AndZeroWhenNeverSaved()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("keycount-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		Assert.Equal(0, await service.GetKeyCountAsync(app, env, SecretBundleKind.Overwrite));
		Assert.Equal(0, await service.GetKeyCountAsync(app, env, SecretBundleKind.Base));

		var values = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
		var outcome = await service.SaveAsync(env.Id, new Dictionary<string, string>(), null, values);
		Assert.IsType<SaveSuccess>(outcome);

		Assert.Equal(2, await service.GetKeyCountAsync(app, env, SecretBundleKind.Base));
		Assert.Equal(0, await service.GetKeyCountAsync(app, env, SecretBundleKind.Overwrite));
	}

	// GetKeyCountAsync는 10분간 캐싱한다 - SaveAsync가 캐시를 명시적으로 지우지 않으면 저장
	// 직후에도 stale한 개수가 보일 수 있어, 무효화가 실제로 일어나는지 확인한다.
	[Fact]
	public async Task GetKeyCountAsync_ReflectsLatestSave_NotStaleCachedValue()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("keycache-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var firstValues = new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" };
		var firstOutcome = await service.SaveAsync(env.Id, new Dictionary<string, string>(), null, firstValues);
		Assert.IsType<SaveSuccess>(firstOutcome);

		Assert.Equal(2, await service.GetKeyCountAsync(app, env, SecretBundleKind.Base));

		var session = await service.LoadForEditAsync(env.Id);
		var secondValues = new Dictionary<string, string>
			{ ["A"] = "1", ["B"] = "2", ["C"] = "3", ["D"] = "4", ["E"] = "5" };
		var secondOutcome = await service.SaveAsync(env.Id, session.Values, session.BaseETag, secondValues);
		Assert.IsType<SaveSuccess>(secondOutcome);

		Assert.Equal(5, await service.GetKeyCountAsync(app, env, SecretBundleKind.Base));
	}

	[Fact]
	public async Task ListHistoryAsync_ReturnsEmptyList_WhenBundleNeverSaved()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (_, env) = await fixture.RegisterAppAsync("nohistory-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		Assert.Empty(await service.ListHistoryAsync(env.Id, SecretBundleKind.Base));
		Assert.Empty(await service.ListHistoryAsync(env.Id, SecretBundleKind.Overwrite));
	}

	[Fact]
	public async Task ListHistoryAsync_ReturnsNewestFirst_AndLoadVersionAsync_DecryptsEachVersionToItsOwnValues()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("history-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var valuesA = new Dictionary<string, string> { ["A"] = "1" };
		var outcomeA = await service.SaveAsync(env.Id, new Dictionary<string, string>(), null, valuesA);
		Assert.IsType<SaveSuccess>(outcomeA);

		var sessionB = await service.LoadForEditAsync(env.Id);
		var valuesB = new Dictionary<string, string> { ["A"] = "2" };
		var outcomeB = await service.SaveAsync(env.Id, sessionB.Values, sessionB.BaseETag, valuesB);
		Assert.IsType<SaveSuccess>(outcomeB);

		var history = await service.ListHistoryAsync(env.Id);
		Assert.Equal(2, history.Count);
		for (var i = 1; i < history.Count; i++)
		{
			Assert.True(history[i - 1].LastModified >= history[i].LastModified);
		}

		var latestVersion = Assert.Single(history, v => v.IsLatest);
		var oldVersion = Assert.Single(history, v => !v.IsLatest);

		Assert.Equal(valuesB, await service.LoadVersionAsync(env.Id, latestVersion.VersionId));
		Assert.Equal(valuesA, await service.LoadVersionAsync(env.Id, oldVersion.VersionId));
	}

	// ListVersions 응답에는 커스텀 메타데이터가 없어 actorEmail은 버전별 조회로 다시 읽어와야
	// 한다 - 그 왕복이 맞물리는지 확인한다.
	[Fact]
	public async Task ListHistoryAsync_ReturnsActorEmail_ForEachSavedVersion()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("historyactor-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var outcomeA = await service.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "1" },
			actorEmail: "alice@example.com");
		Assert.IsType<SaveSuccess>(outcomeA);

		var sessionB = await service.LoadForEditAsync(env.Id);
		var outcomeB = await service.SaveAsync(
			env.Id, sessionB.Values, sessionB.BaseETag, new Dictionary<string, string> { ["A"] = "2" },
			actorEmail: "bob@example.com");
		Assert.IsType<SaveSuccess>(outcomeB);

		var history = await service.ListHistoryAsync(env.Id);
		var latestVersion = Assert.Single(history, v => v.IsLatest);
		var oldVersion = Assert.Single(history, v => !v.IsLatest);

		Assert.Equal("bob@example.com", latestVersion.ActorEmail);
		Assert.Equal("alice@example.com", oldVersion.ActorEmail);
	}

	[Fact]
	public async Task ListHistoryAsync_ReturnsNullActorEmail_WhenSavedWithoutActorEmail()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("historynoactor-" + Guid.NewGuid().ToString("N")[..8]);
		var service = fixture.CreateService();

		var outcome = await service.SaveAsync(
			env.Id, new Dictionary<string, string>(), null, new Dictionary<string, string> { ["A"] = "1" });
		Assert.IsType<SaveSuccess>(outcome);

		var history = await service.ListHistoryAsync(env.Id);
		var version = Assert.Single(history);
		Assert.Null(version.ActorEmail);
	}

	[Fact]
	public async Task VerificationFailure_RollsBackToPreviousVersion_AndDoesNotAdvanceETag()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var fixture = await Fixture.CreateAsync();
		var (app, env) = await fixture.RegisterAppAsync("rollback-" + Guid.NewGuid().ToString("N")[..8]);
		var normalService = fixture.CreateService();

		var initialValues = new Dictionary<string, string> { ["A"] = "1" };
		var initial = await normalService.SaveAsync(env.Id, new Dictionary<string, string>(), null, initialValues);
		var initialSuccess = Assert.IsType<SaveSuccess>(initial);

		var session = await normalService.LoadForEditAsync(env.Id);
		Assert.Equal(initialValues, session.Values);

		// put 이후 두 번째 GetCurrentAsync 호출(검증 단계)에서만 내용을 깨뜨린다.
		var corruptingService = fixture.CreateService(
			store => new CorruptingSecretObjectStore(store, corruptOnCallNumber: 2));
		var badOutcome = await corruptingService.SaveAsync(env.Id, session.Values, session.BaseETag,
			new Dictionary<string, string> { ["A"] = "2" });
		Assert.IsType<SaveFailed>(badOutcome);

		var afterRollback = await normalService.LoadForEditAsync(env.Id);
		Assert.Equal(initialValues, afterRollback.Values);
		Assert.Equal(initialSuccess.NewETag, afterRollback.BaseETag);
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
			var app = new App
			{
				Id = Guid.NewGuid(), Name = appName, CreatedAt = DateTimeOffset.UtcNow
			};
			var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
			app.Envs.Add(env);
			db.Apps.Add(app);
			await db.SaveChangesAsync();
			return (app, env);
		}

		public SecretBundleService CreateService(Func<ISecretObjectStore, ISecretObjectStore>? wrapStore = null)
		{
			var db = CreateDbContext();
			ISecretObjectStore store = Store;
			if (wrapStore is not null)
			{
				store = wrapStore(store);
			}
			return new SecretBundleService(
				db, store, Kms, Kms, new AuditLogger(db), new PrimaryStorageSettingsStore(db),
				new MemoryCache(new MemoryCacheOptions()));
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

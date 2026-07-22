using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>캐시 히트 여부만 검증한다 - 무효화 경로는
/// SecretBundleServiceInfraTests.GetKeyCountAsync_ReflectsLatestSave_NotStaleCachedValue가 담당한다.</summary>
public class SecretBundleServiceKeyCountCacheTests
{
	[Fact]
	public async Task GetKeyCountAsync_SecondCall_DoesNotHitStoreAgain()
	{
		var store = new CountingSecretObjectStore(entryCount: 2);
		var service = await CreateServiceAsync(store);
		var (app, env) = CreateAppAndEnv();

		var first = await service.GetKeyCountAsync(app, env);
		var second = await service.GetKeyCountAsync(app, env);

		Assert.Equal(2, first);
		Assert.Equal(2, second);
		Assert.Equal(1, store.GetCurrentCallCount);
	}

	[Fact]
	public async Task GetKeyCountAsync_BaseAndOverwrite_CacheIndependently()
	{
		var store = new CountingSecretObjectStore(entryCount: 3);
		var service = await CreateServiceAsync(store);
		var (app, env) = CreateAppAndEnv();

		await service.GetKeyCountAsync(app, env, SecretBundleKind.Base);
		await service.GetKeyCountAsync(app, env, SecretBundleKind.Overwrite);
		await service.GetKeyCountAsync(app, env, SecretBundleKind.Base);
		await service.GetKeyCountAsync(app, env, SecretBundleKind.Overwrite);

		Assert.Equal(2, store.GetCurrentCallCount);
	}

	[Fact]
	public async Task GetKeyCountAsync_DifferentEnvs_CacheIndependently()
	{
		var store = new CountingSecretObjectStore(entryCount: 1);
		var service = await CreateServiceAsync(store);
		var app = new App
		{
			Id = Guid.NewGuid(), Name = "app", CreatedAt = DateTimeOffset.UtcNow
		};
		var envA = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev, App = app };
		var envB = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Staging, App = app };

		await service.GetKeyCountAsync(app, envA);
		await service.GetKeyCountAsync(app, envB);
		await service.GetKeyCountAsync(app, envA);
		await service.GetKeyCountAsync(app, envB);

		Assert.Equal(2, store.GetCurrentCallCount);
	}

	private static (App App, Env Env) CreateAppAndEnv()
	{
		var app = new App
		{
			Id = Guid.NewGuid(), Name = "app", CreatedAt = DateTimeOffset.UtcNow
		};
		var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev, App = app };
		return (app, env);
	}

	private static async Task<SecretBundleService> CreateServiceAsync(ISecretObjectStore store)
	{
		var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString())
			.Options);
		var kms = new NotSupportedKmsKeyOperations();
		var auditLogger = new NotSupportedAuditLogger();
		var cache = new MemoryCache(new MemoryCacheOptions());
		var primaryStorageSettingsStore = new PrimaryStorageSettingsStore(db);
		await primaryStorageSettingsStore.SaveAsync(null, "bucket");
		return new SecretBundleService(db, store, kms, kms, auditLogger, primaryStorageSettingsStore, cache);
	}

	/// <summary>GetKeyCountAsync는 dotenv 항목 수만 세고 값은 건드리지 않으므로, sops
	/// 메타데이터(lastmodified/mac)만 있으면 실제 KMS 암호문 없이도 파싱이 통과한다.</summary>
	private sealed class CountingSecretObjectStore(Int32 entryCount) : ISecretObjectStore
	{
		public Int32 GetCurrentCallCount { get; private set; }

		public Task<StoredSecretObject?> GetCurrentAsync(
			string bucket, string key, CancellationToken cancellationToken = default)
		{
			GetCurrentCallCount++;
			var lines = Enumerable.Range(0, entryCount).Select(i => $"KEY{i}=value{i}");
			var content = string.Join('\n', lines) +
				"\nsops_lastmodified=2026-01-01T00:00:00Z\nsops_mac=ENC[fake]\n";
			return Task.FromResult<StoredSecretObject?>(new StoredSecretObject(content, "etag", "v1"));
		}

		public Task<PutSecretObjectResult> PutAsync(
			string bucket, string key, string content, string? actorEmail = null,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task RestoreVersionAsync(
			string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<List<SecretObjectVersion>> ListVersionsAsync(
			string bucket, string key, bool includeActorEmail = false,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<string> GetVersionContentAsync(
			string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task DeleteVersionAsync(
			string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class NotSupportedKmsKeyOperations : S3EnvManager.Sops.IKmsKeyOperations
	{
		public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
			string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<byte[]> EncryptAsync(
			string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();

		public Task<byte[]> DecryptAsync(
			string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}

	private sealed class NotSupportedAuditLogger : IAuditLogger
	{
		public Task LogAsync(string eventType, string? actorUserId, Guid? appId, string? details,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException();
	}
}
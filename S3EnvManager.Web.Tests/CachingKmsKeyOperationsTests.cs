using System.Diagnostics.Metrics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>KMS free tier 절감용 Decrypt 캐시가 지켜야 할 불변조건을 검증한다. 핵심은
/// CachingKmsKeyOperations.cs의 주석이 주장하는 것 - 캐시 키에 encryption context가 빠지면
/// 캐시가 KMS의 context 검증을 몰래 건너뛰게 된다.</summary>
public class CachingKmsKeyOperationsTests
{
	private const string AdminArn = "arn:aws:kms:ap-northeast-2:000000000000:key/fake-admin";
	private const string OtherArn = "arn:aws:kms:ap-northeast-2:000000000000:key/fake-other";

	private static (CachingKmsKeyOperations Cached, CountingKmsKeyOperations Inner) Create()
	{
		var inner = new CountingKmsKeyOperations(new FakeKmsKeyOperations());
		return (new CachingKmsKeyOperations(inner, new MemoryCache(new MemoryCacheOptions())), inner);
	}

	private static Dictionary<string, string> Context(string app) => new() { ["app"] = app };

	[Fact]
	public async Task Decrypt_WithSameArnBlobAndContext_HitsCache_AndCallsKmsOnce()
	{
		var (cached, inner) = Create();
		var context = Context("alpha");
		var (plaintext, blob) = await cached.GenerateDataKeyAsync(AdminArn, context);

		var first = await cached.DecryptAsync(AdminArn, blob, context);
		var second = await cached.DecryptAsync(AdminArn, blob, context);

		Assert.Equal(plaintext, first);
		Assert.Equal(plaintext, second);
		Assert.Equal(1, inner.DecryptCalls);
	}

	// 이 테스트가 이 파일의 존재 이유다. context가 캐시 키에서 빠지면 두 번째 호출이 캐시에
	// 적중해 평문을 돌려주고, KMS가 거부했어야 할 요청이 통과한다.
	[Fact]
	public async Task Decrypt_WithDifferentContext_DoesNotReuseCachedPlaintext()
	{
		var (cached, inner) = Create();
		var (_, blob) = await cached.GenerateDataKeyAsync(AdminArn, Context("alpha"));

		await cached.DecryptAsync(AdminArn, blob, Context("alpha"));
		Assert.Equal(1, inner.DecryptCalls);

		// 같은 ARN·같은 blob이지만 context가 다르다 - KMS(그리고 이를 흉내내는 Fake)는 거부한다.
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => cached.DecryptAsync(AdminArn, blob, Context("beta")));

		// 캐시가 삼킨 게 아니라 실제로 아래까지 내려가 거부당했는지 확인한다.
		Assert.Equal(2, inner.DecryptCalls);
	}

	[Fact]
	public async Task Decrypt_WithDifferentCmkArn_DoesNotReuseCachedPlaintext()
	{
		var (cached, inner) = Create();
		var context = Context("alpha");
		var (_, blob) = await cached.GenerateDataKeyAsync(AdminArn, context);

		await cached.DecryptAsync(AdminArn, blob, context);
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => cached.DecryptAsync(OtherArn, blob, context));

		Assert.Equal(2, inner.DecryptCalls);
	}

	// GenerateDataKey/Encrypt는 매번 새 값이 필요하므로 절대 캐싱하면 안 된다.
	[Fact]
	public async Task GenerateDataKeyAndEncrypt_AreNeverCached()
	{
		var (cached, inner) = Create();
		var context = Context("alpha");

		var (firstKey, firstBlob) = await cached.GenerateDataKeyAsync(AdminArn, context);
		var (secondKey, secondBlob) = await cached.GenerateDataKeyAsync(AdminArn, context);
		Assert.Equal(2, inner.GenerateDataKeyCalls);
		Assert.NotEqual(firstBlob, secondBlob);
		Assert.NotEqual(firstKey, secondKey);

		var firstWrap = await cached.EncryptAsync(AdminArn, firstKey, context);
		var secondWrap = await cached.EncryptAsync(AdminArn, firstKey, context);
		Assert.Equal(2, inner.EncryptCalls);
		Assert.NotEqual(firstWrap, secondWrap);
	}

	// SizeLimit이 걸린 캐시에 Size 없이 Set하면 InvalidOperationException이 난다 - Program.cs가
	// 전용 캐시에 SizeLimit을 거는 이상, 이 데코레이터는 항상 Size를 지정해야 한다.
	[Fact]
	public async Task Decrypt_WorksWithSizeLimitedCache()
	{
		var inner = new CountingKmsKeyOperations(new FakeKmsKeyOperations());
		var sizeLimited = new MemoryCache(new MemoryCacheOptions
		{
			SizeLimit = CachingKmsKeyOperations.DecryptCacheSizeLimit,
		});
		var cached = new CachingKmsKeyOperations(inner, sizeLimited);

		var context = Context("alpha");
		var (plaintext, blob) = await cached.GenerateDataKeyAsync(AdminArn, context);

		Assert.Equal(plaintext, await cached.DecryptAsync(AdminArn, blob, context));
		Assert.Equal(plaintext, await cached.DecryptAsync(AdminArn, blob, context));
		Assert.Equal(1, inner.DecryptCalls);
	}

	// 편집 세션 하나를 버티지 못하던 5분에서 올린 값이다 - 회귀로 다시 줄어들면 잡는다.
	[Fact]
	public void DecryptCacheDuration_IsLongEnoughForAnEditSession()
	{
		Assert.True(
			CachingKmsKeyOperations.DecryptCacheDuration >= TimeSpan.FromMinutes(30),
			$"TTL이 {CachingKmsKeyOperations.DecryptCacheDuration}로 줄었습니다 - 편집 세션 중 재-Decrypt가 발생합니다.");
	}

	// 계측이 실제로 값을 내보내는지 확인한다 - 절감 조치의 효과를 숫자로 볼 수 없으면
	// 이후 K1~K4는 검증이 불가능하다.
	[Fact]
	public async Task Metrics_CountAwsBoundCallsAndCacheOutcomes()
	{
		using var services = new ServiceCollection().AddMetrics().BuildServiceProvider();
		var metrics = new KmsMetrics(services.GetRequiredService<IMeterFactory>());
		var cached = new CachingKmsKeyOperations(
			new FakeKmsKeyOperations(), new MemoryCache(new MemoryCacheOptions()), metrics);

		var recorded = new List<(string Instrument, string Tag, Int64 Value)>();
		using var listener = new MeterListener();
		listener.InstrumentPublished = (instrument, l) =>
		{
			if (instrument.Meter.Name == KmsMetrics.MeterName)
			{
				l.EnableMeasurementEvents(instrument);
			}
		};
		listener.SetMeasurementEventCallback<Int64>((instrument, value, tags, _) =>
		{
			var tag = tags.Length > 0 ? tags[0].Value?.ToString() ?? "" : "";
			lock (recorded)
			{
				recorded.Add((instrument.Name, tag, value));
			}
		});
		listener.Start();

		var context = Context("alpha");
		var (key, blob) = await cached.GenerateDataKeyAsync(AdminArn, context);
		await cached.EncryptAsync(AdminArn, key, context);
		await cached.DecryptAsync(AdminArn, blob, context);   // miss
		await cached.DecryptAsync(AdminArn, blob, context);   // hit

		Int64 Total(string instrument, string tag) =>
			recorded.Where(r => r.Instrument == instrument && r.Tag == tag).Sum(r => r.Value);

		Assert.Equal(1, Total("s3envmanager.kms.calls", "generate_data_key"));
		Assert.Equal(1, Total("s3envmanager.kms.calls", "encrypt"));
		// 캐시 적중은 AWS까지 나가지 않으므로 decrypt 호출 수는 2가 아니라 1이어야 한다.
		Assert.Equal(1, Total("s3envmanager.kms.calls", "decrypt"));
		Assert.Equal(1, Total("s3envmanager.kms.decrypt_cache", "miss"));
		Assert.Equal(1, Total("s3envmanager.kms.decrypt_cache", "hit"));
	}

	private sealed class CountingKmsKeyOperations(IKmsKeyOperations inner) : IKmsKeyOperations
	{
		public Int32 GenerateDataKeyCalls { get; private set; }
		public Int32 EncryptCalls { get; private set; }
		public Int32 DecryptCalls { get; private set; }

		public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
			string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default)
		{
			GenerateDataKeyCalls++;
			return inner.GenerateDataKeyAsync(cmkArn, encryptionContext, cancellationToken);
		}

		public Task<byte[]> EncryptAsync(
			string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default)
		{
			EncryptCalls++;
			return inner.EncryptAsync(cmkArn, plaintextKey, encryptionContext, cancellationToken);
		}

		public Task<byte[]> DecryptAsync(
			string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default)
		{
			DecryptCalls++;
			return inner.DecryptAsync(cmkArn, ciphertextBlob, encryptionContext, cancellationToken);
		}
	}
}

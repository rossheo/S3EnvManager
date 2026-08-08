using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// AWS KMS free tier(월 2만 요청) 절감용 데코레이터 - 같은 ciphertext blob을 반복 Decrypt하는
// 호출(편집 화면 재진입, 히스토리 반복 조회, 충돌 병합 등)만 짧은 TTL로 캐싱한다.
// GenerateDataKey/Encrypt는 매번 새 값이 필요하므로 그대로 통과시킨다. 평문은 프로세스 메모리에만
// 있고 절대 영속화하지 않는다(IDataKeyCache와 동일한 보안 모델).
// metrics는 선택 - 이 데코레이터를 직접 조립하는 테스트가 계측 없이도 쓸 수 있게 둔다.
public sealed class CachingKmsKeyOperations(
	IKmsKeyOperations inner, IMemoryCache cache, KmsMetrics? metrics = null) : IKmsKeyOperations
{
	private static readonly TimeSpan DecryptCacheDuration = TimeSpan.FromMinutes(5);

	public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
		string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		metrics?.RecordGenerateDataKey();
		return inner.GenerateDataKeyAsync(cmkArn, encryptionContext, cancellationToken);
	}

	public Task<byte[]> EncryptAsync(
		string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		metrics?.RecordEncrypt();
		return inner.EncryptAsync(cmkArn, plaintextKey, encryptionContext, cancellationToken);
	}

	public async Task<byte[]> DecryptAsync(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var cacheKey = BuildCacheKey(cmkArn, ciphertextBlob, encryptionContext);
		if (cache.TryGetValue(cacheKey, out byte[]? cachedPlaintext))
		{
			metrics?.RecordDecryptCacheHit();
			return cachedPlaintext!;
		}

		metrics?.RecordDecryptCacheMiss();
		metrics?.RecordDecrypt();
		var plaintext = await inner.DecryptAsync(cmkArn, ciphertextBlob, encryptionContext, cancellationToken)
			.ConfigureAwait(false);
		cache.Set(cacheKey, plaintext, DecryptCacheDuration);
		return plaintext;
	}

	// KMS는 wrap 시 쓰인 encryption context와 다르면 Decrypt 자체를 거부한다 - context를 키에서
	// 빼면 캐시가 그 검증을 몰래 건너뛰게 되므로 반드시 포함한다.
	private static string BuildCacheKey(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext)
	{
		var context = string.Join(',', encryptionContext.OrderBy(kv => kv.Key, StringComparer.Ordinal)
			.Select(kv => $"{kv.Key}={kv.Value}"));
		return $"kms-decrypt:{cmkArn}:{Convert.ToBase64String(ciphertextBlob)}:{context}";
	}
}

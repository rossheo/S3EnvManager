using Microsoft.Extensions.Caching.Memory;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// AWS KMS free tier(월 2만 요청) 절감용 데코레이터 - 같은 ciphertext blob을 반복 Decrypt하는
// 호출(편집 화면 재진입, 히스토리 반복 조회, 충돌 병합 등)만 캐싱한다.
// GenerateDataKey/Encrypt는 매번 새 값이 필요하므로 그대로 통과시킨다. 평문은 프로세스 메모리에만
// 있고 절대 영속화하지 않는다(IDataKeyCache와 동일한 보안 모델).
// metrics는 선택 - 이 데코레이터를 직접 조립하는 테스트가 계측 없이도 쓸 수 있게 둔다.
public sealed class CachingKmsKeyOperations(
	IKmsKeyOperations inner, IMemoryCache cache, KmsMetrics? metrics = null) : IKmsKeyOperations
{
	// 처음에는 5분이었는데 편집 세션 하나도 못 버티는 값이었다 - 화면에서 값을 확인하고 잠시 뒤
	// 저장하면 이미 만료돼 Decrypt가 다시 나갔다. 30분이면 편집 세션 하나가 Decrypt 1회로 끝난다.
	//
	// 대가는 두 가지다. (1) 평문 데이터 키가 메모리에 더 오래 머문다 - 같은 프로세스의
	// IDataKeyCache가 이미 프로세스 수명 내내 무기한으로 평문을 들고 있으므로 새로 생기는
	// 노출은 아니다. (2) **KMS 쪽 권한 회수가 늦게 반영된다** - 사고 대응으로 CMK를 비활성화하거나
	// kms:Decrypt 권한을 회수해도, 이미 캐시에 있는 ciphertext blob은 최대 이 시간만큼 계속
	// 열린다(그동안 kms.calls에도 잡히지 않는다). 즉시 끊어야 하면 프로세스를 재기동해야 한다.
	// 절대 만료를 쓴다(슬라이딩 아님) - 계속 쓰이는 키라도 노출 창은 상한이 있어야 한다.
	public static readonly TimeSpan DecryptCacheDuration = TimeSpan.FromMinutes(30);

	// TTL을 늘리면 상주량도 함께 늘어난다. 엔트리 하나를 크기 1로 세어 "최대 엔트리 수"로 제한한다
	// (전용 MemoryCache에만 SizeLimit이 걸려 있고, 여기 Set은 항상 Size를 지정한다).
	public const Int64 DecryptCacheSizeLimit = 2_000;

	/// <summary>전용 Decrypt 캐시를 DI에서 꺼낼 때 쓰는 키(Program.cs 등록과 짝).</summary>
	public const string CacheServiceKey = "kms-decrypt-cache";

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
		cache.Set(cacheKey, plaintext, new MemoryCacheEntryOptions
		{
			AbsoluteExpirationRelativeToNow = DecryptCacheDuration,
			Size = 1,
		});
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

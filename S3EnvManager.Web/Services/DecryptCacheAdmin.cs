using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace S3EnvManager.Web.Services;

/// <summary>Decrypt 캐시(<see cref="CachingKmsKeyOperations"/>)를 관리자 화면에서 즉시 비우는
/// 통로. TTL을 <see cref="CachingKmsKeyOperations.DecryptCacheDuration"/>만큼 늘린 대가로
/// KMS 쪽 권한 회수(CMK 비활성화, kms:Decrypt 회수 등)가 캐시에 반영되기까지 그만큼 늦어진다.
///
/// **프로세스 재기동과 같지 않다.** 이 캐시만 비운다 - ReusableDataKeyCache(재사용 데이터 키,
/// 최대 10분)와 IDataKeyCache(평문 데이터 키, 프로세스 수명 내내 무기한)는 그대로 남는다.
/// 평문을 완전히 끊어야 하는 사고라면 여전히 프로세스 재기동이 필요하다.</summary>
public interface IDecryptCacheAdmin
{
	void Clear();
}

public sealed class DecryptCacheAdmin(
	[FromKeyedServices(CachingKmsKeyOperations.CacheServiceKey)] MemoryCache cache) : IDecryptCacheAdmin
{
	public void Clear() => cache.Clear();
}

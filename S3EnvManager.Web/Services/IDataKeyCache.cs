using System.Collections.Concurrent;

namespace S3EnvManager.Web.Services;

// DB에는 ciphertext만 저장하고 평문은 메모리에만 캐싱한다. 요청마다 KMS를 재호출하지 않도록 싱글톤.
public interface IDataKeyCache
{
	bool TryGet(Guid dataKeyId, out byte[] plaintextKey);

	void Set(Guid dataKeyId, byte[] plaintextKey);
}

public sealed class DataKeyCache : IDataKeyCache
{
	private readonly ConcurrentDictionary<Guid, byte[]> _cache = new();

	public bool TryGet(Guid dataKeyId, out byte[] plaintextKey) =>
		_cache.TryGetValue(dataKeyId, out plaintextKey!);

	public void Set(Guid dataKeyId, byte[] plaintextKey) => _cache[dataKeyId] = plaintextKey;
}
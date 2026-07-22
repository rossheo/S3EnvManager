namespace S3EnvManager.Web.Services;

/// <summary>스레드 안전한 인메모리 저장소. DB 영속화는 <see cref="IPrimaryStorageSettingsStore"/>가
/// 별도로 담당하고, 이 저장소는 그 값을 앱 기동 시 또는 화면 저장 시 넘겨받아 메모리에만 캐싱한다.</summary>
public sealed class RuntimePrimaryStorageOverride : IRuntimePrimaryStorageOverride
{
	private readonly Lock _lock = new();
	private StorageEndpointSettings? _current;

	public bool IsSet
	{
		get
		{
			lock (_lock)
			{
				return _current is not null;
			}
		}
	}

	public void Set(StorageEndpointSettings settings)
	{
		lock (_lock)
		{
			_current = settings;
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_current = null;
		}
	}

	public StorageEndpointSettings? Get()
	{
		lock (_lock)
		{
			return _current;
		}
	}
}
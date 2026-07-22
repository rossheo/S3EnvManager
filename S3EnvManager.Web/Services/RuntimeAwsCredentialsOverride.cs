namespace S3EnvManager.Web.Services;

/// <summary>스레드 안전한 인메모리 저장소. 이 클래스 자체는 DB/파일에 절대 쓰지 않는다 - DB
/// 영속화는 <see cref="IAwsBootstrapCredentialStore"/>가 별도로 담당하고, 이 저장소는 그
/// 값을 앱 기동 시 또는 화면 저장 시 넘겨받아 메모리에만 캐싱한다.</summary>
public sealed class RuntimeAwsCredentialsOverride : IRuntimeAwsCredentialsOverride
{
	private readonly Lock _lock = new();
	private (string AccessKeyId, string SecretAccessKey)? _current;

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

	public void Set(string accessKeyId, string secretAccessKey)
	{
		lock (_lock)
		{
			_current = (accessKeyId, secretAccessKey);
		}
	}

	public void Clear()
	{
		lock (_lock)
		{
			_current = null;
		}
	}

	public (string AccessKeyId, string SecretAccessKey)? Get()
	{
		lock (_lock)
		{
			return _current;
		}
	}
}
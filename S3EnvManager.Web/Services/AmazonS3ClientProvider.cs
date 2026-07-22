using Amazon.S3;

namespace S3EnvManager.Web.Services;

// 자동 프로비저닝을 실행하기 전까지는 클라이언트를 만들지 않는다 - 설정 없이 조용히 다른 걸
// 가리키는 것보다 명확하게 실패하는 편이 낫다.
public sealed class AmazonS3ClientProvider(
	IRuntimeAwsCredentialsOverride adminCredentialOverride,
	IRuntimePrimaryStorageOverride primaryStorageOverride)
	: IAmazonS3ClientProvider
{
	private readonly Lock _lock = new();
	private IAmazonS3? _client;
	private StorageEndpointSettings? _builtFor;
	private bool _hasBuilt;

	public IAmazonS3 GetClient()
	{
		var current = primaryStorageOverride.Get()
			?? throw new InvalidOperationException("주 저장소가 아직 설정되지 않았습니다. /settings/bootstrap에서 자동 프로비저닝을 실행하세요.");

		lock (_lock)
		{
			// 이전 클라이언트를 Dispose하지 않는다 - 진행 중인 요청이 있을 수 있고 GC에 맡겨도 무해하다.
			if (!_hasBuilt || !Equals(_builtFor, current))
			{
				_client = StorageEndpointClientFactory.BuildClient(current, adminCredentialOverride);
				_builtFor = current;
				_hasBuilt = true;
			}
			return _client!;
		}
	}
}
namespace S3EnvManager.Web.Services;

// 값이 비어 있으면(자동 프로비저닝 미실행) IAmazonS3ClientProvider가 클라이언트 생성을 거부한다.
public interface IRuntimePrimaryStorageOverride
{
	bool IsSet { get; }

	void Set(StorageEndpointSettings settings);

	void Clear();

	StorageEndpointSettings? Get();
}
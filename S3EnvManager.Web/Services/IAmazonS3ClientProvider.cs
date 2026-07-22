using Amazon.S3;

namespace S3EnvManager.Web.Services;

// 리전 설정은 클라이언트 생성 시점에 고정되어 나중에 적용할 수 없으므로, 오버라이드가 바뀌면
// 클라이언트를 다시 만든다.
public interface IAmazonS3ClientProvider
{
	IAmazonS3 GetClient();
}
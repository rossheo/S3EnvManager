namespace S3EnvManager.Web.Services;

// AWS SDK 클라이언트가 실제로 참조하는 자격증명은 이 프로세스 메모리 오버라이드뿐이다(DB를
// 직접 읽지 않는다). 앱 기동 시 IAwsBootstrapCredentialStore에서 여기로 로드한다.
public interface IRuntimeAwsCredentialsOverride
{
	bool IsSet { get; }

	void Set(string accessKeyId, string secretAccessKey);

	void Clear();

	(string AccessKeyId, string SecretAccessKey)? Get();
}
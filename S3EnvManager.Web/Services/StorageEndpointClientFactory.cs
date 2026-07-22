using Amazon;
using Amazon.Runtime;
using Amazon.S3;

namespace S3EnvManager.Web.Services;

// 자격증명은 항상 admin 부트스트랩 자격증명을 쓴다 - 자동 프로비저닝이 유일한 설정 경로다.
public static class StorageEndpointClientFactory
{
	public static AmazonS3Config BuildConfig(StorageEndpointSettings settings)
	{
		var config = new AmazonS3Config();
		if (!string.IsNullOrWhiteSpace(settings.Region))
		{
			config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
		}
		return config;
	}

	public static AmazonS3Client BuildClient(
		StorageEndpointSettings settings, IRuntimeAwsCredentialsOverride adminCredentialOverride)
		=> new(new OverridableAwsCredentials(adminCredentialOverride), BuildConfig(settings));
}

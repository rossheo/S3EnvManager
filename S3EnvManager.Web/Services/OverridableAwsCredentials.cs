using Amazon.Runtime;

namespace S3EnvManager.Web.Services;

// app role KMS 클라이언트는 fallbackOverrideStore로 admin 자격증명을 재사용할 수 있다 - admin
// 정책에 app CMK에 대한 kms:Encrypt가 이미 포함돼 있어(CMK 제거 재래핑용) 새 권한이 필요 없다.
public sealed class OverridableAwsCredentials(
	IRuntimeAwsCredentialsOverride overrideStore,
	IRuntimeAwsCredentialsOverride? fallbackOverrideStore = null) : AWSCredentials
{
	public override ImmutableCredentials GetCredentials()
	{
		if (overrideStore.Get() is { } value)
		{
			return new ImmutableCredentials(value.AccessKeyId, value.SecretAccessKey, token: null);
		}

		if (fallbackOverrideStore?.Get() is { } fallbackValue)
		{
			return new ImmutableCredentials(fallbackValue.AccessKeyId, fallbackValue.SecretAccessKey, token: null);
		}

#pragma warning disable CS0618 // FallbackCredentialsFactory is obsolete but its replacement
		// (DefaultAWSCredentialsIdentityResolver) isn't resolvable in this SDK version's public
		// surface here; this is still the standard "resolve via the default chain" call.
		return FallbackCredentialsFactory.GetCredentials().GetCredentials();
#pragma warning restore CS0618
	}
}
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

// SecretBundleService/CmkRegistryService가 전부 이 규칙을 공유해야 한다. S3EnvManager.Configuration
// (Application 쪽, 별도 어셈블리)이 독립적으로 같은 규칙을 구현하므로 접미사를 바꿀 때는 그쪽과 맞춰야 한다.
internal static class SecretObjectKeys
{
	public static string Locate(App app, Env env, SecretBundleKind kind)
	{
		var suffix = kind == SecretBundleKind.Base ? ".env" : ".overwrite.env";
		return $"{app.Name}/{env.Name.ToObjectSegment()}{suffix}";
	}
}
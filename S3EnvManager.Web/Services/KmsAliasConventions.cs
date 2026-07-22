namespace S3EnvManager.Web.Services;

public static class KmsAliasConventions
{
	public const string ManagedAliasPrefix = "alias/s3envmanager";

	// admin 정책의 KmsUseManagedKeys 문이 KMS 권한을 스코프하는 리소스 태그 - 이 태그 없이는
	// CMK가 실제로 쓸모없으므로 등록/생성 시점에 반드시 붙인다.
	public const string ManagedTagKey = "s3envmanager-managed";

	public static readonly IReadOnlyDictionary<string, string> ManagedTag =
		new Dictionary<string, string> { [ManagedTagKey] = "true" };
}

namespace S3EnvManager.Web.Services;

/// <summary>base와 overwrite만 병합한 값이라, 서버가 모르는 2단(로컬 env var)이 실제로
/// 덮어쓴 키에서는 실제 런타임 값과 다를 수 있다. <see cref="SecretMerge"/>(동시 편집 충돌 처리)와는
/// 무관한 순수 2-way override다.</summary>
public static class EffectiveSecretMerge
{
	public static Dictionary<string, string> Merge(
		IReadOnlyDictionary<string, string> baseValues, IReadOnlyDictionary<string, string> overwriteValues)
	{
		var merged = new Dictionary<string, string>(baseValues);
		foreach (var (key, value) in overwriteValues)
		{
			merged[key] = value;
		}
		return merged;
	}
}
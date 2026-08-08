namespace S3EnvManager.Cli;

/// <summary>get이 --key를 여러 개 받았을 때 번들에서 요청한 키만 골라낸다. 요청 순서를
/// 보존하고, 없는 키는 따로 모아 호출자가 --allow-missing 여부로 판단하게 한다.</summary>
public static class KeySelection
{
	public static (Dictionary<string, string> Selected, List<string> Missing) Select(
		IReadOnlyDictionary<string, string> values, IReadOnlyList<string> keys)
	{
		var selected = new Dictionary<string, string>(StringComparer.Ordinal);
		var missing = new List<string>();
		foreach (var key in keys)
		{
			if (values.TryGetValue(key, out var value))
			{
				selected[key] = value;
			}
			else
			{
				missing.Add(key);
			}
		}
		return (selected, missing);
	}
}

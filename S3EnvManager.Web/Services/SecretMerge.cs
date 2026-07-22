namespace S3EnvManager.Web.Services;

public sealed record ConflictItem(string Key, string? Mine, string? Theirs);

public sealed record MergeReport(
	IReadOnlyDictionary<string, string> MergedValues,
	IReadOnlyList<string> AutoAppliedTheirsKeys,
	IReadOnlyList<ConflictItem> RealConflicts);

// base(편집 시작 시점 값), mine(내가 편집한 값), theirs(그 사이 remote에 반영된 값) 3-way merge.
public static class SecretMerge
{
	public static MergeReport Merge(
		IReadOnlyDictionary<string, string> baseValues,
		IReadOnlyDictionary<string, string> mineValues,
		IReadOnlyDictionary<string, string> theirsValues)
	{
		var merged = new Dictionary<string, string>();
		var autoAppliedTheirs = new List<string>();
		var conflicts = new List<ConflictItem>();

		var allKeys = new HashSet<string>(baseValues.Keys);
		allKeys.UnionWith(mineValues.Keys);
		allKeys.UnionWith(theirsValues.Keys);

		foreach (var key in allKeys)
		{
			baseValues.TryGetValue(key, out var baseValue);
			mineValues.TryGetValue(key, out var mineValue);
			theirsValues.TryGetValue(key, out var theirsValue);

			if (mineValue == theirsValue)
			{
				if (mineValue is not null)
				{
					merged[key] = mineValue;
				}
				continue;
			}

			if (mineValue == baseValue)
			{
				if (theirsValue is not null)
				{
					merged[key] = theirsValue;
					autoAppliedTheirs.Add(key);
				}
				else
				{
					autoAppliedTheirs.Add(key);
				}
				continue;
			}

			if (theirsValue == baseValue)
			{
				if (mineValue is not null)
				{
					merged[key] = mineValue;
				}
				continue;
			}

			// 한쪽 삭제 + 다른 쪽 수정은 삭제 우선(자동 해결), 그 외엔 사용자 선택이 필요한 진짜 충돌.
			if (mineValue is null || theirsValue is null)
			{
				continue;
			}

			conflicts.Add(new ConflictItem(key, mineValue, theirsValue));
		}

		return new MergeReport(merged, autoAppliedTheirs, conflicts);
	}
}
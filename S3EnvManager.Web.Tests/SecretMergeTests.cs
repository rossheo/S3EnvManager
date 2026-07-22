using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class SecretMergeTests
{
	[Fact]
	public void OnlyMineChanged_KeepsMineValue()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "2" },
			theirsValues: new Dictionary<string, string> { ["A"] = "1" });

		Assert.Equal("2", report.MergedValues["A"]);
		Assert.Empty(report.AutoAppliedTheirsKeys);
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void OnlyTheirsChanged_AutoAdoptsTheirsValue_AndReportsBanner()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "1" },
			theirsValues: new Dictionary<string, string> { ["A"] = "3" });

		Assert.Equal("3", report.MergedValues["A"]);
		Assert.Contains("A", report.AutoAppliedTheirsKeys);
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void BothChangedToSameValue_IsNotAConflict()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "9" },
			theirsValues: new Dictionary<string, string> { ["A"] = "9" });

		Assert.Equal("9", report.MergedValues["A"]);
		Assert.Empty(report.AutoAppliedTheirsKeys);
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void BothChangedToDifferentValues_IsARealConflict()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "2" },
			theirsValues: new Dictionary<string, string> { ["A"] = "3" });

		Assert.False(report.MergedValues.ContainsKey("A"));
		var conflict = Assert.Single(report.RealConflicts);
		Assert.Equal("A", conflict.Key);
		Assert.Equal("2", conflict.Mine);
		Assert.Equal("3", conflict.Theirs);
	}

	[Fact]
	public void MineDeletedTheirsModified_DeleteWins()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string>(),
			theirsValues: new Dictionary<string, string> { ["A"] = "2" });

		Assert.False(report.MergedValues.ContainsKey("A"));
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void TheirsDeletedMineModified_DeleteWins()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "2" },
			theirsValues: new Dictionary<string, string>());

		Assert.False(report.MergedValues.ContainsKey("A"));
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void KeyAddedByMineOnly_IsKeptWithoutConflict()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string>(),
			mineValues: new Dictionary<string, string> { ["NEW"] = "x" },
			theirsValues: new Dictionary<string, string>());

		Assert.Equal("x", report.MergedValues["NEW"]);
		Assert.Empty(report.RealConflicts);
	}

	[Fact]
	public void KeyAddedByBothWithDifferentValues_IsARealConflict()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string>(),
			mineValues: new Dictionary<string, string> { ["NEW"] = "x" },
			theirsValues: new Dictionary<string, string> { ["NEW"] = "y" });

		var conflict = Assert.Single(report.RealConflicts);
		Assert.Equal("NEW", conflict.Key);
	}

	[Fact]
	public void UnrelatedKeys_AreUnaffectedByEachOther()
	{
		var report = SecretMerge.Merge(
			baseValues: new Dictionary<string, string> { ["A"] = "1", ["B"] = "1", ["C"] = "1" },
			mineValues: new Dictionary<string, string> { ["A"] = "2", ["B"] = "1", ["C"] = "1" },
			theirsValues: new Dictionary<string, string> { ["A"] = "1", ["B"] = "9", ["C"] = "1" });

		Assert.Equal("2", report.MergedValues["A"]);
		Assert.Equal("9", report.MergedValues["B"]);
		Assert.Equal("1", report.MergedValues["C"]);
		Assert.Contains("B", report.AutoAppliedTheirsKeys);
		Assert.Empty(report.RealConflicts);
	}
}
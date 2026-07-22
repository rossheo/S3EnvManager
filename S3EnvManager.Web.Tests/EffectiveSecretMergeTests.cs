using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>overwrite가 base를 이기는 방향이 핵심 - 뒤집혀도 빌드는 통과하므로 테스트로 고정한다.</summary>
public class EffectiveSecretMergeTests
{
	[Fact]
	public void OverwriteValueWinsOverBaseValue_ForSharedKey()
	{
		var baseValues = new Dictionary<string, string> { ["A"] = "1", ["B"] = "1" };
		var overwriteValues = new Dictionary<string, string> { ["B"] = "2", ["C"] = "3" };

		var merged = EffectiveSecretMerge.Merge(baseValues, overwriteValues);

		Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2", ["C"] = "3" }, merged);
	}

	[Fact]
	public void ReturnsOverwriteOnly_WhenBaseNeverSaved()
	{
		var merged = EffectiveSecretMerge.Merge(
			new Dictionary<string, string>(), new Dictionary<string, string> { ["A"] = "1" });

		Assert.Equal(new Dictionary<string, string> { ["A"] = "1" }, merged);
	}

	[Fact]
	public void ReturnsBaseOnly_WhenOverwriteEmpty()
	{
		var merged = EffectiveSecretMerge.Merge(
			new Dictionary<string, string> { ["A"] = "1" }, new Dictionary<string, string>());

		Assert.Equal(new Dictionary<string, string> { ["A"] = "1" }, merged);
	}
}
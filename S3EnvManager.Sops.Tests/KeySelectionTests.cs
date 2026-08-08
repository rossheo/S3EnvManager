using S3EnvManager.Cli;
using Xunit;

namespace S3EnvManager.Sops.Tests;

public class KeySelectionTests
{
	private static readonly Dictionary<string, string> Bundle = new(StringComparer.Ordinal)
	{
		["FOO"] = "1",
		["BAR"] = "2",
		["BAZ"] = "3",
	};

	[Fact]
	public void Select_ReturnsRequestedKeysOnly()
	{
		var (selected, missing) = KeySelection.Select(Bundle, ["BAZ", "FOO"]);

		Assert.Empty(missing);
		Assert.Equal(new Dictionary<string, string> { ["BAZ"] = "3", ["FOO"] = "1" }, selected);
	}

	[Fact]
	public void Select_PreservesRequestedOrder()
	{
		var (selected, _) = KeySelection.Select(Bundle, ["BAZ", "BAR", "FOO"]);

		Assert.Equal(["BAZ", "BAR", "FOO"], selected.Keys);
	}

	[Fact]
	public void Select_CollectsMissingKeys_WithoutFailing()
	{
		var (selected, missing) = KeySelection.Select(Bundle, ["FOO", "NOPE", "ALSO_NOPE"]);

		Assert.Equal(["NOPE", "ALSO_NOPE"], missing);
		Assert.Equal("1", Assert.Single(selected).Value);
	}

	// 키 이름은 대소문자를 구분한다 - 번들의 키가 그대로 환경변수 이름이 되기 때문.
	[Fact]
	public void Select_IsCaseSensitive()
	{
		var (selected, missing) = KeySelection.Select(Bundle, ["foo"]);

		Assert.Empty(selected);
		Assert.Equal("foo", Assert.Single(missing));
	}
}

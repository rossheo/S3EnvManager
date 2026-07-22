using S3EnvManager.Database.Models;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class AppIndexFilterTests
{
	private static readonly App SampleApp = new()
	{
		Name = "MyApp",
	};

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void EmptyOrWhitespaceSearchText_MatchesEverything(string? searchText)
	{
		Assert.True(Components.Pages.Apps.Index.MatchesSearch(SampleApp, searchText));
	}

	[Theory]
	[InlineData("myapp")]
	[InlineData("MYAPP")]
	[InlineData("App")]
	public void MatchesByName_CaseInsensitive(string searchText)
	{
		Assert.True(Components.Pages.Apps.Index.MatchesSearch(SampleApp, searchText));
	}

	[Fact]
	public void ReturnsFalse_WhenNameDoesNotMatch()
	{
		Assert.False(Components.Pages.Apps.Index.MatchesSearch(SampleApp, "no-such-app"));
	}
}
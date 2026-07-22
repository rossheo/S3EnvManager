using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class SecretValuePreviewFormatterTests
{
	[Theory]
	[InlineData("123")]
	[InlineData("hunter2")]
	public void IsLongValue_ReturnsFalse_ForShortSingleLineValues(string value) =>
		Assert.False(SecretValuePreviewFormatter.IsLongValue(value));

	[Fact]
	public void IsLongValue_ReturnsTrue_WhenValueContainsNewline() =>
		Assert.True(SecretValuePreviewFormatter.IsLongValue("line1\nline2"));

	[Fact]
	public void IsLongValue_ReturnsTrue_WhenSingleLineExceedsThreshold() =>
		Assert.True(SecretValuePreviewFormatter.IsLongValue(new string('a', 81)));

	[Fact]
	public void Summarize_IncludesLineCountAndSize_ForMultilineValue()
	{
		var summary = SecretValuePreviewFormatter.Summarize(
			"-----BEGIN CERTIFICATE-----\nabc\n-----END CERTIFICATE-----");

		Assert.Contains("3줄", summary);
		Assert.Contains("-----BEGIN CERTIFICATE-----", summary);
	}

	[Fact]
	public void Summarize_TruncatesLongFirstLine()
	{
		var summary = SecretValuePreviewFormatter.Summarize(new string('a', 200));

		Assert.Contains("…", summary);
		Assert.DoesNotContain(new string('a', 200), summary);
	}
}
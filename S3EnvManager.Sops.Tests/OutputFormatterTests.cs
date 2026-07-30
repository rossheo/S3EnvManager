using S3EnvManager.Cli;
using Xunit;

namespace S3EnvManager.Sops.Tests;

public class OutputFormatterTests
{
	[Fact]
	public void FormatGetAll_Json_ContainsKeyAndValue()
	{
		var values = new Dictionary<string, string> { ["FOO"] = "bar" };

		var output = OutputFormatter.FormatGetAll(values, OutputFormat.Json);

		Assert.Contains("\"FOO\"", output, StringComparison.Ordinal);
		Assert.Contains("\"bar\"", output, StringComparison.Ordinal);
	}

	[Fact]
	public void FormatGetAll_Dotenv_ProducesKeyEqualsValueLines()
	{
		var values = new Dictionary<string, string> { ["FOO"] = "bar", ["BAZ"] = "a=b;c" };

		var output = OutputFormatter.FormatGetAll(values, OutputFormat.Dotenv);

		Assert.Contains("FOO=bar\n", output, StringComparison.Ordinal);
		// 값 안의 '='는 그대로 남아야 한다(첫 '='만 구분자로 취급하는 소비자를 위해).
		Assert.Contains("BAZ=a=b;c\n", output, StringComparison.Ordinal);
	}

	[Fact]
	public void FormatGetAll_Dotenv_ValueWithNewline_ThrowsOutputFormatError()
	{
		var values = new Dictionary<string, string> { ["FOO"] = "line1\nline2" };

		var ex = Assert.Throws<CliException>(() => OutputFormatter.FormatGetAll(values, OutputFormat.Dotenv));

		Assert.Equal(ExitCode.OutputFormatError, ex.ExitCode);
	}
}

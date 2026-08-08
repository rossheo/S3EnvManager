using S3EnvManager.Cli;
using Xunit;

namespace S3EnvManager.Sops.Tests;

public class CliOptionsTests
{
	private static readonly string[] CommonArgs = ["--bucket", "b", "--app", "a", "--env", "e"];

	[Fact]
	public void Parse_NoArgs_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(() => CliOptions.Parse([]));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_UnknownCommand_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(() => CliOptions.Parse(["delete"]));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_GetWithoutKey_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(() => CliOptions.Parse(["get", .. CommonArgs]));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_MissingBucketAppEnv_ThrowsArgumentError()
	{
		// 실제 프로세스 환경변수(S3ENVMANAGER_*)가 개발 머신에 설정돼 있을 수 있으므로,
		// "아무 값도 없음"을 결정론적으로 재현하려면 환경변수 조회 자체를 빈 값으로 주입한다.
		var ex = Assert.Throws<CliException>(() => CliOptions.Parse(["get-all"], _ => null));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_UnknownFlag_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(() =>
			CliOptions.Parse(["get-all", .. CommonArgs, "--bogus"]));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_UnknownFormatValue_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(() =>
			CliOptions.Parse(["get-all", .. CommonArgs, "--format", "xml"]));
		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_ValidGet_ReturnsExpectedOptions()
	{
		var options = CliOptions.Parse(["get", .. CommonArgs, "--key", "FOO", "--allow-missing"]);

		Assert.Equal(CliCommand.Get, options.Command);
		Assert.Equal("b", options.Bucket);
		Assert.Equal("a", options.AppName);
		Assert.Equal("e", options.EnvSegment);
		Assert.Equal("FOO", Assert.Single(options.Keys));
		Assert.True(options.AllowMissing);
		Assert.False(options.AllowMissingBundle);
	}

	[Fact]
	public void Parse_Get_WithRepeatedKey_CollectsAllInOrder()
	{
		var options = CliOptions.Parse(
			["get", .. CommonArgs, "--key", "FOO", "--key", "BAR", "--key", "BAZ"]);

		Assert.Equal(["FOO", "BAR", "BAZ"], options.Keys);
	}

	// `--key "$MYVAR"`에서 변수가 비어 있는 흔한 실수. 인자 오류로 잡지 않으면 "키 없음"(5)이나
	// --allow-missing과 함께 0으로 끝나 호출자가 설정 실수를 알아채지 못한다.
	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void Parse_Get_WithBlankKey_ThrowsArgumentError(string blankKey)
	{
		var ex = Assert.Throws<CliException>(
			() => CliOptions.Parse(["get", .. CommonArgs, "--key", blankKey]));

		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_Get_WithDuplicateKey_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(
			() => CliOptions.Parse(["get", .. CommonArgs, "--key", "FOO", "--key", "FOO"]));

		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_GetAll_WithKey_ThrowsArgumentError()
	{
		var ex = Assert.Throws<CliException>(
			() => CliOptions.Parse(["get-all", .. CommonArgs, "--key", "FOO"]));

		Assert.Equal(ExitCode.ArgumentError, ex.ExitCode);
	}

	[Fact]
	public void Parse_GetAll_DefaultFormatIsJson()
	{
		var options = CliOptions.Parse(["get-all", .. CommonArgs]);

		Assert.Equal(OutputFormat.Json, options.Format);
	}

	[Fact]
	public void Parse_GetAll_FormatDotenv_AndAllowMissingBundle()
	{
		var options = CliOptions.Parse(["get-all", .. CommonArgs, "--format", "dotenv", "--allow-missing-bundle"]);

		Assert.Equal(OutputFormat.Dotenv, options.Format);
		Assert.True(options.AllowMissingBundle);
	}
}

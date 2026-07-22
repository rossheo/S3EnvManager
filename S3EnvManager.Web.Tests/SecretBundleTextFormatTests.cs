using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>여러 줄 값(PEM/SSH 키 등)이 한 줄 표현을 왕복해도 원래 값 그대로 복원돼야 한다.</summary>
public class SecretBundleTextFormatTests
{
	[Fact]
	public void RoundTrips_MultilineValue()
	{
		var original = new Dictionary<string, string>
		{
			["CERT_PEM"] = "-----BEGIN CERTIFICATE-----\nMIIB...\n-----END CERTIFICATE-----\n",
		};

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void RoundTrips_ValueContainingLiteralBackslashN()
	{
		// "\n" 두 글자(백슬래시+n)가 실제 개행 이스케이프와 충돌하지 않아야 한다.
		var original = new Dictionary<string, string> { ["PATH"] = @"C:\notes\newfile.txt" };

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void RoundTrips_ValueContainingEqualsSign()
	{
		var original = new Dictionary<string, string> { ["QUERY"] = "a=1&b=2" };

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void RoundTrips_EmptyValue()
	{
		var original = new Dictionary<string, string> { ["EMPTY"] = "" };

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void RoundTrips_CarriageReturnAndCrLf()
	{
		var original = new Dictionary<string, string> { ["CRLF"] = "line1\r\nline2\rline3" };

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void RoundTrips_MultipleKeysPreservesOrder()
	{
		var original = new List<KeyValuePair<string, string>>
		{
			new("B", "2"),
			new("A", "1"),
		};

		var formatted = SecretBundleTextFormat.Format(original);
		var ok = SecretBundleTextFormat.TryParse(formatted, out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(original, parsed);
	}

	[Fact]
	public void TryParse_RejectsLineWithoutEqualsSign()
	{
		var ok = SecretBundleTextFormat.TryParse("NOEQUALSHERE\n", out _, out var error);

		Assert.False(ok);
		Assert.Contains("'='", error);
	}

	[Fact]
	public void TryParse_RejectsDuplicateKeys()
	{
		var ok = SecretBundleTextFormat.TryParse("A=1\nA=2\n", out _, out var error);

		Assert.False(ok);
		Assert.Contains("중복", error);
	}

	[Fact]
	public void TryParse_RejectsInvalidKeyName()
	{
		var ok = SecretBundleTextFormat.TryParse("sops_mac=tampered\n", out _, out var error);

		Assert.False(ok);
		Assert.NotNull(error);
	}

	[Fact]
	public void TryParse_RejectsDanglingBackslashAtEndOfValue()
	{
		var ok = SecretBundleTextFormat.TryParse("A=trailing\\\n", out _, out var error);

		Assert.False(ok);
		Assert.NotNull(error);
	}

	[Fact]
	public void TryParse_RejectsUnknownEscapeSequence()
	{
		var ok = SecretBundleTextFormat.TryParse("A=bad\\xescape\n", out _, out var error);

		Assert.False(ok);
		Assert.NotNull(error);
	}

	[Fact]
	public void TryParse_SkipsBlankLines()
	{
		var ok = SecretBundleTextFormat.TryParse("A=1\n\nB=2\n", out var parsed, out var error);

		Assert.True(ok, error);
		Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
			parsed.ToDictionary(kv => kv.Key, kv => kv.Value));
	}

	[Fact]
	public void Format_EmptyCollection_ProducesEmptyString()
	{
		Assert.Equal(string.Empty, SecretBundleTextFormat.Format(new Dictionary<string, string>()));
	}
}
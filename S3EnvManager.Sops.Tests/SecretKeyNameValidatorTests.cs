using S3EnvManager.Sops;
using Xunit;

namespace S3EnvManager.Sops.Tests;

public class SecretKeyNameValidatorTests
{
	[Theory]
	[InlineData("FOO")]
	[InlineData("FOO_BAR")]
	[InlineData("App__Setting")]
	[InlineData("a")]
	public void Validate_AcceptsOrdinaryKeys(string key)
	{
		Assert.Null(SecretKeyNameValidator.Validate(key));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void Validate_RejectsEmpty(string? key)
	{
		Assert.NotNull(SecretKeyNameValidator.Validate(key));
	}

	[Theory]
	[InlineData("FOO=BAR")]
	[InlineData("FOO\nBAR")]
	[InlineData("FOO\rBAR")]
	[InlineData("sops_mac")]
	[InlineData("SOPS_MAC")]
	[InlineData("sops_kms__list_0__map_arn")]
	public void Validate_RejectsFormatBreakingOrReservedKeys(string key)
	{
		Assert.NotNull(SecretKeyNameValidator.Validate(key));
	}
}
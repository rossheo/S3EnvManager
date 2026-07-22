using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class CmkArnValidatorTests
{
	[Theory]
	[InlineData("arn:aws:kms:ap-northeast-2:123456789012:key/1234abcd-12ab-34cd-56ef-1234567890ab")]
	[InlineData("arn:aws-us-gov:kms:us-gov-west-1:123456789012:key/1234abcd-12ab-34cd-56ef-1234567890ab")]
	public void Validate_AcceptsWellFormedKmsArns(string arn)
	{
		Assert.Null(CmkArnValidator.Validate(arn));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("not-an-arn")]
	[InlineData("arn:aws:s3:::some-bucket")]
	[InlineData("arn:aws:kms:ap-northeast-2:123456789012:alias/my-key")]
	[InlineData("arn:aws:kms:ap-northeast-2:12345:key/1234abcd-12ab-34cd-56ef-1234567890ab")]
	public void Validate_RejectsMalformedArns(string? arn)
	{
		Assert.NotNull(CmkArnValidator.Validate(arn));
	}
}
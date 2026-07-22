using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>버킷 이름이 문자열 보간으로 IAM 정책 JSON에 삽입돼도 안전한 문자만 오도록 보장한다.</summary>
public class BucketNameValidatorTests
{
	[Theory]
	[InlineData("my-bucket")]
	[InlineData("my.bucket.name")]
	[InlineData("bucket123")]
	[InlineData("a23")]
	public void Validate_AcceptsAllowedNames(string bucket)
	{
		Assert.Null(BucketNameValidator.Validate(bucket));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Validate_RejectsEmptyOrWhitespace(string? bucket)
	{
		Assert.NotNull(BucketNameValidator.Validate(bucket));
	}

	[Theory]
	[InlineData("ab")]
	public void Validate_RejectsTooShort(string bucket)
	{
		Assert.NotNull(BucketNameValidator.Validate(bucket));
	}

	[Fact]
	public void Validate_RejectsTooLong()
	{
		var tooLong = new string('a', 64);
		Assert.NotNull(BucketNameValidator.Validate(tooLong));
	}

	[Theory]
	[InlineData("My-Bucket")]
	[InlineData("my_bucket")]
	[InlineData("-my-bucket")]
	[InlineData("my-bucket-")]
	[InlineData("my\"bucket")] // JSON 삽입 방지 핵심 케이스
	[InlineData("my\\bucket")]
	public void Validate_RejectsDisallowedCharactersOrShape(string bucket)
	{
		Assert.NotNull(BucketNameValidator.Validate(bucket));
	}

	[Fact]
	public void Validate_RejectsConsecutivePeriods()
	{
		Assert.NotNull(BucketNameValidator.Validate("my..bucket"));
	}

	[Fact]
	public void Validate_RejectsIpAddressFormat()
	{
		Assert.NotNull(BucketNameValidator.Validate("192.168.1.1"));
	}
}
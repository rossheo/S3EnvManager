using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>제약의 근거는 IAM UserName 규칙과 "s3envmanager-app-" 접두사 길이다.</summary>
public class AppNameValidatorTests
{
	[Theory]
	[InlineData("myapp")]
	[InlineData("my-app_1")]
	[InlineData("My.App+1,2=3@x")]
	public void Validate_AcceptsAllowedCharacters(string name)
	{
		Assert.Null(AppNameValidator.Validate(name));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Validate_RejectsEmptyOrWhitespace(string? name)
	{
		Assert.NotNull(AppNameValidator.Validate(name));
	}

	[Theory]
	[InlineData("my app")]
	[InlineData("my/app")] // S3 키에는 허용되지만 IAM UserName엔 안 됨
	[InlineData("앱이름")]
	[InlineData("my*app")]
	public void Validate_RejectsDisallowedCharacters(string name)
	{
		Assert.NotNull(AppNameValidator.Validate(name));
	}

	[Fact]
	public void Validate_AllowsExactlyMaxLength_ButRejectsOneCharOver()
	{
		var atMax = new string('a', AppNameValidator.MaxLength);
		var overMax = new string('a', AppNameValidator.MaxLength + 1);

		Assert.Null(AppNameValidator.Validate(atMax));
		Assert.NotNull(AppNameValidator.Validate(overMax));
	}
}
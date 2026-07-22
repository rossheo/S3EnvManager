using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class OverridableAwsCredentialsTests
{
	[Fact]
	public void GetCredentials_UsesOverride_WhenSet()
	{
		var store = new RuntimeAwsCredentialsOverride();
		store.Set("AKIATEST", "secret-value");
		var credentials = new OverridableAwsCredentials(store);

		var result = credentials.GetCredentials();

		Assert.Equal("AKIATEST", result.AccessKey);
		Assert.Equal("secret-value", result.SecretKey);
		Assert.True(string.IsNullOrEmpty(result.Token));
	}

	[Fact]
	public void Clear_RemovesOverride_AndIsSetReflectsState()
	{
		var store = new RuntimeAwsCredentialsOverride();
		Assert.False(store.IsSet);

		store.Set("AKIATEST", "secret-value");
		Assert.True(store.IsSet);

		store.Clear();
		Assert.False(store.IsSet);
		Assert.Null(store.Get());
	}

	[Fact]
	public void GetCredentials_FallsBackToSecondStore_WhenPrimaryNotSet()
	{
		var primary = new RuntimeAwsCredentialsOverride();
		var fallback = new RuntimeAwsCredentialsOverride();
		fallback.Set("AKIAFALLBACK", "fallback-secret");
		var credentials = new OverridableAwsCredentials(primary, fallback);

		var result = credentials.GetCredentials();

		Assert.Equal("AKIAFALLBACK", result.AccessKey);
		Assert.Equal("fallback-secret", result.SecretKey);
	}

	[Fact]
	public void GetCredentials_PrefersPrimaryStore_OverFallback()
	{
		var primary = new RuntimeAwsCredentialsOverride();
		primary.Set("AKIAPRIMARY", "primary-secret");
		var fallback = new RuntimeAwsCredentialsOverride();
		fallback.Set("AKIAFALLBACK", "fallback-secret");
		var credentials = new OverridableAwsCredentials(primary, fallback);

		var result = credentials.GetCredentials();

		Assert.Equal("AKIAPRIMARY", result.AccessKey);
	}
}
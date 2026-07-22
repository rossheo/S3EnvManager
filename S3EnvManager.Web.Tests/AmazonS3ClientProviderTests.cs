using Amazon.S3;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class AmazonS3ClientProviderTests
{
	[Fact]
	public void GetClient_Throws_WhenNothingConfigured()
	{
		var provider = new AmazonS3ClientProvider(
			new RuntimeAwsCredentialsOverride(), new RuntimePrimaryStorageOverride());

		Assert.Throws<InvalidOperationException>(() => provider.GetClient());
	}

	[Fact]
	public void GetClient_ReturnsSameInstance_WhenOverrideUnchanged()
	{
		var storageOverride = new RuntimePrimaryStorageOverride();
		storageOverride.Set(new StorageEndpointSettings("us-east-1"));
		var provider = new AmazonS3ClientProvider(new RuntimeAwsCredentialsOverride(), storageOverride);

		var first = provider.GetClient();
		var second = provider.GetClient();

		Assert.Same(first, second);
	}

	[Fact]
	public void GetClient_RebuildsClient_WhenOverrideChanges()
	{
		var storageOverride = new RuntimePrimaryStorageOverride();
		storageOverride.Set(new StorageEndpointSettings("us-east-1"));
		var provider = new AmazonS3ClientProvider(new RuntimeAwsCredentialsOverride(), storageOverride);

		var first = provider.GetClient();
		storageOverride.Set(new StorageEndpointSettings("ap-northeast-2"));
		var second = provider.GetClient();

		Assert.NotSame(first, second);
		var config = Assert.IsType<AmazonS3Config>(second.Config);
		Assert.Equal("ap-northeast-2", config.RegionEndpoint?.SystemName);
	}

	[Fact]
	public void GetClient_ReturnsSameInstance_WhenOverrideSetToEquivalentValue()
	{
		var storageOverride = new RuntimePrimaryStorageOverride();
		var settings = new StorageEndpointSettings("us-east-1");
		storageOverride.Set(settings);
		var provider = new AmazonS3ClientProvider(new RuntimeAwsCredentialsOverride(), storageOverride);

		var first = provider.GetClient();
		storageOverride.Set(settings with { });
		var second = provider.GetClient();

		Assert.Same(first, second);
	}

	[Fact]
	public void GetClient_Throws_WhenOverrideClearedAfterBeingSet()
	{
		var storageOverride = new RuntimePrimaryStorageOverride();
		var provider = new AmazonS3ClientProvider(new RuntimeAwsCredentialsOverride(), storageOverride);

		storageOverride.Set(new StorageEndpointSettings("us-east-1"));
		provider.GetClient();
		storageOverride.Clear();

		Assert.Throws<InvalidOperationException>(() => provider.GetClient());
	}
}
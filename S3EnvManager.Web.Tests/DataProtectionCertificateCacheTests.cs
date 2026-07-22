using System.Security.Cryptography.X509Certificates;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class DataProtectionCertificateCacheTests
{
	[Fact]
	public void GetActive_WhenEmpty_ReturnsNull()
	{
		var cache = new DataProtectionCertificateCache();
		Assert.Null(cache.GetActive());
		Assert.Empty(cache.GetAll());
	}

	[Fact]
	public void ReplaceSnapshot_OrdersByNotBeforeDescending_SoGetActiveIsNewest()
	{
		var cache = new DataProtectionCertificateCache();
		var (older, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(
			1, "pw", new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-30)));
		var (newer, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(1, "pw", TimeProvider.System);

		cache.ReplaceSnapshot([older, newer]);

		Assert.Equal(newer.Thumbprint, cache.GetActive()!.Thumbprint);
		Assert.Equal(2, cache.GetAll().Count);
	}

	private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class PrimaryStorageSettingsStoreTests
{
	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
			.Options);

	private static IPrimaryStorageSettingsStore CreateStore(ApplicationDbContext db) =>
		new PrimaryStorageSettingsStore(db);

	[Fact]
	public async Task SaveAndGet_RoundTrips()
	{
		var store = CreateStore(CreateDbContext());

		await store.SaveAsync("us-east-1", "s3envmanager-prod");

		var result = await store.GetAsync();

		Assert.NotNull(result);
		Assert.Equal("us-east-1", result.Region);
	}

	[Fact]
	public async Task Get_WhenNotSaved_ReturnsNull()
	{
		var store = CreateStore(CreateDbContext());
		Assert.Null(await store.GetAsync());
	}

	[Fact]
	public async Task SaveWithBucket_ThenGetLastProvisionedBucket_RoundTrips()
	{
		var store = CreateStore(CreateDbContext());

		await store.SaveAsync("us-east-1", "s3envmanager-prod");

		Assert.Equal("s3envmanager-prod", await store.GetLastProvisionedBucketAsync());
	}

	[Fact]
	public async Task Save_OverwritesPreviouslyStoredBucket()
	{
		// 자동 프로비저닝 재실행 시 bucket도 region처럼 최신 값으로 갱신돼야 한다.
		var store = CreateStore(CreateDbContext());
		await store.SaveAsync("us-east-1", "s3envmanager-prod");

		await store.SaveAsync("ap-northeast-2", "s3envmanager-new");

		Assert.Equal("s3envmanager-new", await store.GetLastProvisionedBucketAsync());
		Assert.Equal("ap-northeast-2", (await store.GetAsync())?.Region);
	}

	[Fact]
	public async Task GetLastProvisionedBucket_WhenNotSaved_ReturnsNull()
	{
		var store = CreateStore(CreateDbContext());
		Assert.Null(await store.GetLastProvisionedBucketAsync());
	}
}

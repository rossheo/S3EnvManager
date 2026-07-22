using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>KMS 순환 참조 회피: DataProtection 암호화가 로컬 대칭키만으로 동작해야 한다.</summary>
public class AwsBootstrapCredentialStoreTests
{
	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
			.Options);

	private static IAwsBootstrapCredentialStore CreateStore(ApplicationDbContext db) =>
		new AwsBootstrapCredentialStore(db, new EphemeralDataProtectionProvider());

	[Fact]
	public async Task SaveAndGet_RoundTrips_PerRole()
	{
		var db = CreateDbContext();
		var store = CreateStore(db);

		await store.SaveAsync(CmkRole.Admin, "AKIAADMIN", "admin-secret");
		await store.SaveAsync(CmkRole.App, "AKIAAPP", "app-secret");

		var admin = await store.GetAsync(CmkRole.Admin);
		var app = await store.GetAsync(CmkRole.App);

		Assert.Equal(("AKIAADMIN", "admin-secret"), admin);
		Assert.Equal(("AKIAAPP", "app-secret"), app);
	}

	[Fact]
	public async Task Get_WhenNotSaved_ReturnsNull()
	{
		var store = CreateStore(CreateDbContext());
		Assert.Null(await store.GetAsync(CmkRole.Admin));
	}

	[Fact]
	public async Task Save_Twice_OverwritesPreviousValue()
	{
		var db = CreateDbContext();
		var store = CreateStore(db);

		await store.SaveAsync(CmkRole.Admin, "first-id", "first-secret");
		await store.SaveAsync(CmkRole.Admin, "second-id", "second-secret");

		Assert.Equal(("second-id", "second-secret"), await store.GetAsync(CmkRole.Admin));
	}

	[Fact]
	public async Task Clear_RemovesStoredValue()
	{
		var db = CreateDbContext();
		var store = CreateStore(db);

		await store.SaveAsync(CmkRole.App, "AKIAAPP", "app-secret");
		await store.ClearAsync(CmkRole.App);

		Assert.Null(await store.GetAsync(CmkRole.App));
	}

	[Fact]
	public async Task StoredValue_IsNotPlaintext_InTheDatabase()
	{
		var db = CreateDbContext();
		var store = CreateStore(db);

		await store.SaveAsync(CmkRole.Admin, "AKIAADMIN", "super-secret-value");

		var raw = await db.AwsBootstrapCredentials.AsNoTracking().SingleAsync(c => c.Role == CmkRole.Admin);
		Assert.DoesNotContain("super-secret-value", raw.ProtectedSecretAccessKey, StringComparison.Ordinal);
	}
}
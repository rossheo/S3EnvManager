using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
		CreateStore(db, new EphemeralDataProtectionProvider());

	private static IAwsBootstrapCredentialStore CreateStore(
		ApplicationDbContext db, IDataProtectionProvider dataProtectionProvider) =>
		new AwsBootstrapCredentialStore(
			db, dataProtectionProvider, NullLogger<AwsBootstrapCredentialStore>.Instance);

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

	// 키링이 사라진 상태로 기동하면 Unprotect가 CryptographicException을 던진다. 예전에는 그게
	// 그대로 Program.cs 기동 경로로 올라가 호스트가 죽어, 자격증명을 다시 등록할 화면조차 뜨지
	// 않았다 - "미설정"으로 내려앉아야 복구가 가능하다.
	[Fact]
	public async Task Get_WhenProtectionKeyIsGone_ReturnsNull_InsteadOfThrowing()
	{
		var db = CreateDbContext();
		await CreateStore(db, new EphemeralDataProtectionProvider())
			.SaveAsync(CmkRole.Admin, "AKIAADMIN", "admin-secret");

		// 다른 키링을 가진 provider = 원래 키가 사라진 상황.
		var storeWithLostKeyRing = CreateStore(db, new EphemeralDataProtectionProvider());

		Assert.Null(await storeWithLostKeyRing.GetAsync(CmkRole.Admin));

		// 행 자체는 남겨둔다 - 키링을 되살릴 여지를 없애지 않기 위함이고, 재등록은 SaveAsync가 덮어쓴다.
		Assert.True(await db.AwsBootstrapCredentials.AnyAsync(c => c.Role == CmkRole.Admin));
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
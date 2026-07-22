using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class DataProtectionCertificateStoreTests
{
	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
			.Options);

	[Fact]
	public async Task LoadAllAsync_WhenNoRows_ReturnsEmpty()
	{
		var certs = await DataProtectionCertificateStore.LoadAllAsync(CreateDbContext(), "pw");
		Assert.Empty(certs);
	}

	[Fact]
	public async Task IssueAndSaveAsync_ThenLoadAllAsync_RoundTrips()
	{
		var db = CreateDbContext();

		var issued = await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, "pw", validityYears: 2, TimeProvider.System);

		var loaded = await DataProtectionCertificateStore.LoadAllAsync(db, "pw");
		Assert.Single(loaded);
		Assert.Equal(issued.Thumbprint, loaded[0].Thumbprint);
	}

	[Fact]
	public async Task IssueAndSaveAsync_TwiceKeepsBothRows_AppendOnly()
	{
		var db = CreateDbContext();

		var first = await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, "pw", validityYears: 2, TimeProvider.System);
		var second = await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, "pw", validityYears: 2, TimeProvider.System);

		var loaded = await DataProtectionCertificateStore.LoadAllAsync(db, "pw");
		Assert.Equal(2, loaded.Count);
		Assert.Contains(loaded, c => c.Thumbprint == first.Thumbprint);
		Assert.Contains(loaded, c => c.Thumbprint == second.Thumbprint);
	}

	[Fact]
	public async Task LoadAllAsync_WhenRowsExistButPasswordIsWrong_ThrowsInsteadOfSilentlyIgnoring()
	{
		var db = CreateDbContext();
		await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, "correct-password", validityYears: 2, TimeProvider.System);

		// 빈 목록을 반환하면 호출자가 "최초 설치"로 오인해 새 인증서를 또 발급하고, 기존 값은
		// 영구히 복호화 불가능해진다 - 반드시 예외로 드러나야 한다.
		await Assert.ThrowsAsync<InvalidOperationException>(
			() => DataProtectionCertificateStore.LoadAllAsync(db, "wrong-password"));
	}
}
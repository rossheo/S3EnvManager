using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>회귀 방지: AppNameValidator가 없으면 등록은 성공하고 나중 자격증명 발급 시점에야
/// 원인 파악 어려운 AWS ValidationException으로 표면화됐다.</summary>
public class AppRegistrationServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task RegisterAsync_RejectsInvalidName_AndPersistsNothing()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var invalidName = "invalid app/name " + Guid.NewGuid().ToString("N")[..8];
		var service = CreateService();

		await Assert.ThrowsAsync<InvalidAppNameException>(() => service.RegisterAsync(invalidName));

		await using var db = CreateDbContext();
		Assert.False(await db.Apps.AnyAsync(a => a.Name == invalidName));
	}

	// 이름 검증이 AWS 호출보다 먼저 일어나므로 아래 인프라 의존은 생성자를 채우는 용도일 뿐이다.
	private static AppRegistrationService CreateService() =>
		new(
			CreateDbContext(),
			new BucketSelfHealService(new FakeBucketComplianceOperations(), new AuditLogger(CreateDbContext())),
			new BucketHealthStatusStore(),
			new PrimaryStorageSettingsStore(CreateDbContext()));

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

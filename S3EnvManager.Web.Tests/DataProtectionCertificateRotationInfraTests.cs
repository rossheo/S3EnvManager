using Microsoft.EntityFrameworkCore;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>pg_advisory_xact_lock을 쓰므로 InMemory 프로바이더로 대체할 수 없어 실 Postgres가 필요하다.</summary>
public class DataProtectionCertificateRotationInfraTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task RotateIfDueAsync_ReturnsDisabled_WhenPasswordNotConfigured()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var options = new DataProtectionCertificateOptions { Password = null };
		var cache = new DataProtectionCertificateCache();
		var auditLogger = new AuditLogger(CreateDbContext());

		var outcome = await DataProtectionCertificateRotationService.RotateIfDueAsync(
			db, options, cache, auditLogger, TimeProvider.System);

		Assert.Equal(DataProtectionCertificateRotationOutcome.Disabled, outcome);
	}

	[Fact]
	public async Task RotateIfDueAsync_DoesNothing_WhenActiveCertificateIsNotNearExpiry()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		await ClearAllCertificatesAsync(db);
		var options = new DataProtectionCertificateOptions
			{ Password = "pw", ValidityYears = 2, RotateBeforeExpiryDays = 365 };
		var cache = new DataProtectionCertificateCache();
		var auditLogger = new AuditLogger(CreateDbContext());

		await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, options.Password!, options.ValidityYears, TimeProvider.System);

		var outcome = await DataProtectionCertificateRotationService.RotateIfDueAsync(
			CreateDbContext(), options, cache, auditLogger, TimeProvider.System);

		Assert.Equal(DataProtectionCertificateRotationOutcome.NotDueYet, outcome);
	}

	[Fact]
	public async Task RotateIfDueAsync_IssuesNewGeneration_WhenActiveCertificateIsNearExpiry_AndUpdatesCacheAndAudit()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		await ClearAllCertificatesAsync(db);
		var options = new DataProtectionCertificateOptions
			{ Password = "pw", ValidityYears = 2, RotateBeforeExpiryDays = 365 };
		var cache = new DataProtectionCertificateCache();
		var auditLogger = new AuditLogger(CreateDbContext());

		var issued = await DataProtectionCertificateStore.IssueAndSaveAsync(
			db, options.Password!, options.ValidityYears, TimeProvider.System);
		var row = await db.DataProtectionCertificates.SingleAsync(c => c.Thumbprint == issued.Thumbprint);
		await db.Database.ExecuteSqlInterpolatedAsync(
			$"UPDATE \"DataProtectionCertificates\" SET \"NotAfter\" = {DateTimeOffset.UtcNow.AddDays(30)} WHERE \"Id\" = {row.Id}");

		var outcome = await DataProtectionCertificateRotationService.RotateIfDueAsync(
			CreateDbContext(), options, cache, auditLogger, TimeProvider.System);

		Assert.Equal(DataProtectionCertificateRotationOutcome.Rotated, outcome);

		await using var verifyDb = CreateDbContext();
		var rows = await verifyDb.DataProtectionCertificates.AsNoTracking().ToListAsync();
		Assert.Equal(2, rows.Count);

		Assert.Equal(2, cache.GetAll().Count);
		Assert.NotEqual(issued.Thumbprint, cache.GetActive()!.Thumbprint);

		var log = await verifyDb.AuditLogs
			.Where(a => a.EventType == AuditEventTypes.DataProtectionCertificateRotated)
			.OrderByDescending(a => a.OccurredAt).FirstAsync();
		Assert.Contains(cache.GetActive()!.Thumbprint, log.Details);
	}

	private static async Task ClearAllCertificatesAsync(ApplicationDbContext db) =>
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataProtectionCertificates\"");

	private static async Task<bool> IsPostgresAvailableAsync()
	{
		try
		{
			await using var connection = new NpgsqlConnection(PostgresConnectionString);
			await connection.OpenAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}
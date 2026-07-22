using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public enum DataProtectionCertificateRotationOutcome
{
	Disabled,
	NotDueYet,
	Rotated,
}

// 최초 부트스트랩 시 잠글 로우 자체가 없을 수 있어(로우 0개), 싱글턴 행의 FOR UPDATE 대신
// 테이블 전체를 대상으로 하는 Postgres advisory lock을 쓴다.
public static class DataProtectionCertificateRotationService
{
	private const Int64 AdvisoryLockKey = 724100381;

	public static async Task<DataProtectionCertificateRotationOutcome> RotateIfDueAsync(
		ApplicationDbContext db,
		DataProtectionCertificateOptions options,
		DataProtectionCertificateCache cache,
		IAuditLogger auditLogger,
		TimeProvider timeProvider,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrEmpty(options.Password))
		{
			return DataProtectionCertificateRotationOutcome.Disabled;
		}

		var strategy = db.Database.CreateExecutionStrategy();
		var outcome = await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);
			await db.Database.ExecuteSqlInterpolatedAsync(
				$"SELECT pg_advisory_xact_lock({AdvisoryLockKey})", cancellationToken).ConfigureAwait(false);

			var latest = await db.DataProtectionCertificates.AsNoTracking()
				.OrderByDescending(c => c.NotBefore)
				.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

			var due = latest is null ||
				timeProvider.GetUtcNow() >= latest.NotAfter - TimeSpan.FromDays(options.RotateBeforeExpiryDays);
			if (!due)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return DataProtectionCertificateRotationOutcome.NotDueYet;
			}

			var issued = await DataProtectionCertificateStore.IssueAndSaveAsync(
				db, options.Password, options.ValidityYears, timeProvider, cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { thumbprint = issued.Thumbprint, notAfter = issued.NotAfter });
			await auditLogger.LogAsync(
				AuditEventTypes.DataProtectionCertificateRotated, actorUserId: null, appId: null, details,
				cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return DataProtectionCertificateRotationOutcome.Rotated;
		}).ConfigureAwait(false);

		if (outcome == DataProtectionCertificateRotationOutcome.Rotated)
		{
			var all = await DataProtectionCertificateStore.LoadAllAsync(db, options.Password, cancellationToken)
				.ConfigureAwait(false);
			cache.ReplaceSnapshot(all);
		}

		return outcome;
	}
}
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

/// <summary>버킷 자가 치유 lifecycle 규칙과 동일하게 180일 보관 기간을 맞춘다.</summary>
public static class AuditLogRetentionService
{
	public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(180);

	public static async Task DeleteExpiredLogsAsync(
		ApplicationDbContext db, TimeProvider timeProvider, CancellationToken cancellationToken = default)
	{
		var cutoff = timeProvider.GetUtcNow() - RetentionPeriod;
		await db.AuditLogs.Where(a => a.OccurredAt <= cutoff).ExecuteDeleteAsync(cancellationToken)
			.ConfigureAwait(false);
	}
}
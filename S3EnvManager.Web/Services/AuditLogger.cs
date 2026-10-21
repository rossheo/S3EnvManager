using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class AuditLogger(ApplicationDbContext db, TimeProvider timeProvider) : IAuditLogger
{
	public async Task LogAsync(
		string eventType, string? actorUserId, Guid? appId, string? details,
		CancellationToken cancellationToken = default)
	{
		db.AuditLogs.Add(new AuditLog
		{
			Id = Guid.NewGuid(),
			OccurredAt = timeProvider.GetUtcNow(),
			ActorUserId = actorUserId,
			EventType = eventType,
			AppId = appId,
			Details = details,
		});
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}
}
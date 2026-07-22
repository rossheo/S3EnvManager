using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class AppDeletionService(
	ApplicationDbContext db, IAppCredentialProvisioner provisioner, IAuditLogger auditLogger)
	: IAppDeletionService
{
	public async Task DeleteAsync(
		Guid appId, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var app = await db.Apps.SingleAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
		if (app.DeletedAt is not null)
		{
			return;
		}

		var activeCredentials = await db.AppCredentials
			.Where(c => c.AppId == appId && c.RevokedAt == null)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		// 액세스 키뿐 아니라 IAM 사용자 자체를 삭제해 재발급 없이는 재사용 불가능하게 한다.
		await provisioner.DeleteUserAsync(app.Name, cancellationToken).ConfigureAwait(false);
		foreach (var credential in activeCredentials)
		{
			credential.RevokedAt = DateTimeOffset.UtcNow;
		}

		app.DeletedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		foreach (var credential in activeCredentials)
		{
			var details = System.Text.Json.JsonSerializer.Serialize(
				new { accessKeyId = credential.AccessKeyId, reason = "AppDeleted" }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.CredentialRevoked, actorUserId, appId, details, cancellationToken)
				.ConfigureAwait(false);
		}
	}
}
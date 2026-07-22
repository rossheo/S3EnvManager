using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class AppCredentialService(
	ApplicationDbContext db,
	IAppCredentialProvisioner provisioner,
	IAppSecretKeyCipher secretKeyCipher,
	IAuditLogger auditLogger,
	IPrimaryStorageSettingsStore primaryStorageSettingsStore) : IAppCredentialService
{
	public async Task<(AppCredential Credential, string SecretAccessKey)> IssueAsync(
		Guid appId, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var app = await db.Apps.AsNoTracking()
			.SingleAsync(a => a.Id == appId, cancellationToken).ConfigureAwait(false);
		// 활성 CMK 하나만이 아니라 등록된 app role CMK 전부를 부여한다 - 승격 이후에도
		// 재저장 전까지 옛 CMK로 감싸진 시크릿이 있을 수 있기 때문이다.
		var appCmkArns = await db.CmkRegistrations.AsNoTracking()
			.Where(c => c.Role == CmkRole.App)
			.Select(c => c.Arn)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		if (appCmkArns.Count == 0)
		{
			throw new InvalidOperationException("app role의 CMK가 등록되어 있지 않습니다. 관리자가 먼저 CMK를 등록해야 합니다.");
		}

		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false)
			?? throw new InvalidOperationException("주 저장소가 아직 프로비저닝되지 않았습니다.");
		var provisioned = await provisioner.IssueAsync(app.Name, bucket, appCmkArns, cancellationToken)
			.ConfigureAwait(false);
		var (ciphertext, dataKeyId) = await secretKeyCipher.EncryptAsync(
			provisioned.SecretAccessKey, cancellationToken)
			.ConfigureAwait(false);

		var credential = new AppCredential
		{
			Id = Guid.NewGuid(),
			AppId = appId,
			AccessKeyId = provisioned.AccessKeyId,
			EncryptedSecretKey = ciphertext,
			DataKeyId = dataKeyId,
			IssuedAt = DateTimeOffset.UtcNow,
		};
		db.AppCredentials.Add(credential);
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		var details = System.Text.Json.JsonSerializer.Serialize(
			new { accessKeyId = credential.AccessKeyId }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(AuditEventTypes.CredentialIssued, actorUserId, appId, details, cancellationToken)
			.ConfigureAwait(false);

		return (credential, provisioned.SecretAccessKey);
	}

	public Task<List<AppCredential>> ListAsync(Guid appId, CancellationToken cancellationToken = default) =>
		db.AppCredentials.AsNoTracking()
			.Where(c => c.AppId == appId)
			.OrderByDescending(c => c.IssuedAt)
			.ToListAsync(cancellationToken);

	public async Task<string> RevealAsync(Guid credentialId, CancellationToken cancellationToken = default)
	{
		var credential = await db.AppCredentials.AsNoTracking()
			.SingleAsync(c => c.Id == credentialId, cancellationToken).ConfigureAwait(false);
		return await secretKeyCipher.DecryptAsync(
			credential.EncryptedSecretKey, credential.DataKeyId, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task RevokeAsync(
		Guid credentialId, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var credential = await db.AppCredentials.Include(c => c.App)
			.SingleAsync(c => c.Id == credentialId, cancellationToken).ConfigureAwait(false);
		if (credential.RevokedAt is not null)
		{
			return;
		}

		await provisioner.RevokeAccessKeyAsync(credential.App!.Name, credential.AccessKeyId, cancellationToken)
			.ConfigureAwait(false);
		credential.RevokedAt = DateTimeOffset.UtcNow;
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		var details = System.Text.Json.JsonSerializer.Serialize(
			new { accessKeyId = credential.AccessKeyId }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.CredentialRevoked, actorUserId, credential.AppId, details, cancellationToken)
			.ConfigureAwait(false);
	}
}
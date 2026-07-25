using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class SharedSecretReferenceService(
	ApplicationDbContext db,
	ISecretBundleService bundleService,
	IAppSecretKeyCipher cipher,
	IAuditLogger auditLogger)
	: ISharedSecretReferenceService
{
	public async Task<IReadOnlyList<SharedSecretSummary>> ListReferencableAsync(
		Guid appId, CancellationToken cancellationToken = default)
	{
		return await db.SharedSecretAppGrants.AsNoTracking()
			.Where(g => g.AppId == appId)
			.Join(db.SharedSecrets.AsNoTracking(), g => g.SharedSecretId, s => s.Id, (g, s) => s)
			.OrderBy(s => s.Name)
			.Select(s => new SharedSecretSummary(s.Id, s.Name, s.Description, s.ExpiresAt, s.UpdatedAt))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyDictionary<string, Guid>> LoadReferencesAsync(
		Guid envId, SecretBundleKind kind, CancellationToken cancellationToken = default)
	{
		var isOverwriteBundle = kind == SecretBundleKind.Overwrite;
		return await db.SharedSecretReferences.AsNoTracking()
			.Where(r => r.EnvId == envId && r.IsOverwriteBundle == isOverwriteBundle)
			.ToDictionaryAsync(r => r.KeyName, r => r.SharedSecretId, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task<SaveOutcome> SaveWithReferencesAsync(
		Guid envId,
		IReadOnlyDictionary<string, string> baseSnapshot,
		string? baseETag,
		IReadOnlyDictionary<string, string> editedOwnValues,
		IReadOnlyDictionary<string, Guid> editedReferences,
		string? actorUserId,
		string? actorEmail,
		SecretBundleKind kind,
		IReadOnlyDictionary<string, DateTimeOffset?>? editedOwnExpirations = null,
		CancellationToken cancellationToken = default)
	{
		if (editedReferences.Count == 0)
		{
			// 참조가 아예 없으면 기존 경로와 완전히 동일하게 동작한다(회귀 없음).
			return await bundleService.SaveAsync(
				envId, baseSnapshot, baseETag, editedOwnValues, actorUserId, actorEmail, kind,
				editedOwnExpirations, editedReferences, cancellationToken).ConfigureAwait(false);
		}

		var collidingKey = editedReferences.Keys.FirstOrDefault(editedOwnValues.ContainsKey);
		if (collidingKey is not null)
		{
			return new SaveFailed($"'{collidingKey}'가 자체 소유 값과 참조에 동시에 존재합니다.");
		}

		var appId = await db.Envs.AsNoTracking()
			.Where(e => e.Id == envId)
			.Select(e => e.AppId)
			.SingleAsync(cancellationToken).ConfigureAwait(false);

		var neededSharedSecretIds = editedReferences.Values.Distinct().ToList();
		var grantedIds = await db.SharedSecretAppGrants.AsNoTracking()
			.Where(g => g.AppId == appId && neededSharedSecretIds.Contains(g.SharedSecretId))
			.Select(g => g.SharedSecretId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		var ungranted = neededSharedSecretIds.Except(grantedIds).ToList();
		if (ungranted.Count > 0)
		{
			return new SaveFailed("허가되지 않은 공유 시크릿을 참조할 수 없습니다. 관리자에게 그랜트를 요청하세요.");
		}

		var secrets = await db.SharedSecrets.AsNoTracking()
			.Where(s => neededSharedSecretIds.Contains(s.Id))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		if (secrets.Count != neededSharedSecretIds.Count)
		{
			return new SaveFailed("참조하려는 공유 시크릿을 찾을 수 없습니다(삭제되었을 수 있습니다).");
		}

		var resolvedValues = new Dictionary<Guid, string>();
		foreach (var secret in secrets)
		{
			resolvedValues[secret.Id] = await cipher
				.DecryptAsync(secret.Ciphertext, secret.DataKeyId, cancellationToken)
				.ConfigureAwait(false);
		}

		var finalValues = new Dictionary<string, string>(editedOwnValues);
		foreach (var (keyName, sharedSecretId) in editedReferences)
		{
			finalValues[keyName] = resolvedValues[sharedSecretId];
		}

		var beforeReferences = await LoadReferencesAsync(envId, kind, cancellationToken)
			.ConfigureAwait(false);

		var outcome = await bundleService.SaveAsync(
			envId, baseSnapshot, baseETag, finalValues, actorUserId, actorEmail, kind,
			editedOwnExpirations, editedReferences, cancellationToken).ConfigureAwait(false);

		if (outcome is SaveSuccess)
		{
			foreach (var (keyName, sharedSecretId) in editedReferences)
			{
				var wasAlreadyReferencing = beforeReferences.TryGetValue(keyName, out var previousId)
					&& previousId == sharedSecretId;
				if (wasAlreadyReferencing)
				{
					continue;
				}

				var details = System.Text.Json.JsonSerializer.Serialize(
					new { sharedSecretId, keyName }, AuditJsonOptions.Default);
				await auditLogger.LogAsync(
					AuditEventTypes.SharedSecretReferenceAttached, actorUserId, appId, details,
					cancellationToken).ConfigureAwait(false);
			}
		}

		return outcome;
	}

	public async Task DetachAsync(
		Guid envId, bool isOverwriteBundle, string keyName, string? actorUserId,
		CancellationToken cancellationToken = default)
	{
		var reference = await db.SharedSecretReferences
			.SingleAsync(
				r => r.EnvId == envId && r.IsOverwriteBundle == isOverwriteBundle && r.KeyName == keyName,
				cancellationToken)
			.ConfigureAwait(false);
		var appId = await db.Envs.AsNoTracking()
			.Where(e => e.Id == envId)
			.Select(e => e.AppId)
			.SingleAsync(cancellationToken).ConfigureAwait(false);

		db.SharedSecretReferences.Remove(reference);
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		var details = System.Text.Json.JsonSerializer.Serialize(
			new { sharedSecretId = reference.SharedSecretId, keyName }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.SharedSecretReferenceDetached, actorUserId, appId, details, cancellationToken)
			.ConfigureAwait(false);
	}
}

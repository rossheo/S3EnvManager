using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class SharedSecretService(
	ApplicationDbContext db, IAppSecretKeyCipher cipher, IAuditLogger auditLogger,
	ISecretBundleService bundleService)
	: ISharedSecretService
{
	public async Task<List<SharedSecretSummary>> ListAsync(CancellationToken cancellationToken = default)
	{
		return await db.SharedSecrets.AsNoTracking()
			.OrderBy(s => s.Name)
			.Select(s => new SharedSecretSummary(s.Id, s.Name, s.Description, s.ExpiresAt, s.UpdatedAt))
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<SharedSecretDetail> LoadForEditAsync(
		Guid id, CancellationToken cancellationToken = default)
	{
		var entity = await db.SharedSecrets.AsNoTracking()
			.SingleAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
		var value = await cipher.DecryptAsync(entity.Ciphertext, entity.DataKeyId, cancellationToken)
			.ConfigureAwait(false);
		return new SharedSecretDetail(entity.Id, entity.Name, entity.Description, value, entity.ExpiresAt);
	}

	public async Task<Guid> CreateAsync(
		string name, string? description, string value, DateTimeOffset? expiresAt,
		string? actorUserId, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			throw new ArgumentException("이름을 입력하세요.", nameof(name));
		}

		var (ciphertext, dataKeyId) = await cipher.EncryptAsync(value, cancellationToken)
			.ConfigureAwait(false);
		var now = DateTimeOffset.UtcNow;
		var entity = new SharedSecret
		{
			Id = Guid.NewGuid(),
			Name = name,
			Description = description,
			Ciphertext = ciphertext,
			DataKeyId = dataKeyId,
			ExpiresAt = expiresAt,
			CreatedAt = now,
			UpdatedAt = now,
			CreatedByUserId = actorUserId,
		};
		db.SharedSecrets.Add(entity);
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		var details = System.Text.Json.JsonSerializer.Serialize(
			new { name }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.SharedSecretCreated, actorUserId, appId: null, details, cancellationToken)
			.ConfigureAwait(false);

		return entity.Id;
	}

	public async Task<SharedSecretUpdateResult> UpdateAsync(
		Guid id, string? description, string? newValue, DateTimeOffset? expiresAt,
		string? actorUserId, CancellationToken cancellationToken = default)
	{
		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다. SecretBundleService.SaveAsync가 Env별로
		// 자기 트랜잭션을 여는 것과 겹칠 수 없으므로(같은 DbContext에서 트랜잭션 중첩 불가),
		// SharedSecret 자체의 값 갱신은 여기서 잠금+커밋까지 마치고, cascade 재materialize는
		// 이 트랜잭션이 끝난 뒤 별도로 실행한다 - 대신 각 Env 저장은 SecretBundleService.SaveAsync
		// 자신의 FOR UPDATE(Env 행) + ETag 검사로 개별적으로 안전하다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var locked = await db.SharedSecrets
				.FromSqlInterpolated($"SELECT * FROM \"SharedSecrets\" WHERE \"Id\" = {id} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);

			locked.Description = description;
			locked.ExpiresAt = expiresAt;
			locked.UpdatedAt = DateTimeOffset.UtcNow;

			if (newValue is not null)
			{
				var (ciphertext, dataKeyId) = await cipher.EncryptAsync(newValue, cancellationToken)
					.ConfigureAwait(false);
				locked.Ciphertext = ciphertext;
				locked.DataKeyId = dataKeyId;
			}

			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { valueChanged = newValue is not null }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.SharedSecretUpdated, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);

		if (newValue is null)
		{
			return new SharedSecretUpdateResult([]);
		}

		var failures = await CascadeMaterializeAsync(id, newValue, actorUserId, cancellationToken)
			.ConfigureAwait(false);
		return new SharedSecretUpdateResult(failures);
	}

	public async Task<SharedSecretUpdateResult> ResyncAsync(
		Guid id, string? actorUserId, CancellationToken cancellationToken = default)
	{
		var entity = await db.SharedSecrets.AsNoTracking()
			.SingleAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
		var currentValue = await cipher.DecryptAsync(entity.Ciphertext, entity.DataKeyId, cancellationToken)
			.ConfigureAwait(false);

		var failures = await CascadeMaterializeAsync(id, currentValue, actorUserId, cancellationToken)
			.ConfigureAwait(false);
		return new SharedSecretUpdateResult(failures);
	}

	// 참조하는 모든 (Env, kind)에 새 값을 재materialize한다. App 단위로 실패를 격리해 계속
	// 진행하고, ETag 충돌은 1회 재시도한다. 멱등이라 값 변경 없이도(ResyncAsync) 안전하게
	// 다시 실행할 수 있다.
	private async Task<List<SharedSecretCascadeFailure>> CascadeMaterializeAsync(
		Guid sharedSecretId, string newValue, string? actorUserId, CancellationToken cancellationToken)
	{
		var references = await db.SharedSecretReferences.AsNoTracking()
			.Where(r => r.SharedSecretId == sharedSecretId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var failures = new List<SharedSecretCascadeFailure>();
		foreach (var group in references.GroupBy(r => (r.EnvId, r.IsOverwriteBundle)))
		{
			var (envId, isOverwriteBundle) = group.Key;
			var kind = isOverwriteBundle ? SecretBundleKind.Overwrite : SecretBundleKind.Base;
			var keyNames = group.Select(r => r.KeyName).ToList();

			try
			{
				var outcome = await MaterializeOneEnvAsync(
					envId, kind, sharedSecretId, keyNames, newValue, actorUserId, cancellationToken)
					.ConfigureAwait(false);
				if (outcome is SaveConflict retryConflict)
				{
					// ETag 경합 1회 재시도.
					outcome = await MaterializeOneEnvAsync(
						envId, kind, sharedSecretId, keyNames, newValue, actorUserId, cancellationToken)
						.ConfigureAwait(false);
				}

				if (outcome is SaveSuccess)
				{
					var appId = await db.Envs.AsNoTracking()
						.Where(e => e.Id == envId).Select(e => e.AppId).SingleAsync(cancellationToken)
						.ConfigureAwait(false);
					var details = System.Text.Json.JsonSerializer.Serialize(
						new { sharedSecretId, keyNames }, AuditJsonOptions.Default);
					await auditLogger.LogAsync(
						AuditEventTypes.SharedSecretCascadeMaterialized, actorUserId, appId, details,
						cancellationToken).ConfigureAwait(false);
				}
				else
				{
					var reason = outcome switch
					{
						SaveFailed failed => failed.Reason,
						SaveConflict => "동시 편집 충돌이 재시도 후에도 해소되지 않았습니다.",
						_ => "알 수 없는 오류",
					};
					failures.Add(new SharedSecretCascadeFailure(envId, isOverwriteBundle, reason));
				}
			}
			catch (Exception ex)
			{
				failures.Add(new SharedSecretCascadeFailure(envId, isOverwriteBundle, ex.Message));
			}
		}

		return failures;
	}

	private async Task<SaveOutcome> MaterializeOneEnvAsync(
		Guid envId, SecretBundleKind kind, Guid sharedSecretId, IReadOnlyList<string> keyNames,
		string newValue, string? actorUserId, CancellationToken cancellationToken)
	{
		var session = await bundleService.LoadForEditAsync(envId, kind, cancellationToken)
			.ConfigureAwait(false);
		var editedValues = new Dictionary<string, string>(session.Values);
		var editedReferences = new Dictionary<string, Guid>();
		foreach (var keyName in keyNames)
		{
			editedValues[keyName] = newValue;
			editedReferences[keyName] = sharedSecretId;
		}

		return await bundleService.SaveAsync(
			envId, session.Values, session.BaseETag, editedValues, actorUserId, actorEmail: null, kind,
			editedExpirations: null, editedReferences, cancellationToken).ConfigureAwait(false);
	}

	public async Task DeleteAsync(
		Guid id, string? actorUserId, CancellationToken cancellationToken = default)
	{
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var locked = await db.SharedSecrets
				.FromSqlInterpolated($"SELECT * FROM \"SharedSecrets\" WHERE \"Id\" = {id} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);

			// 자동 detach: 참조 행만 지운다(값은 이미 각 Env 번들에 materialize돼 있어 그대로
			// 자체 소유 키가 된다) - 절대 dangling 상태를 남기지 않는다. 만료일이 있었다면
			// 잃지 않도록 각 Env의 KeyExpiration으로 승격 복제한다.
			var references = await db.SharedSecretReferences
				.Where(r => r.SharedSecretId == id)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			await PromoteExpirationsForReferencesAsync(locked, references, cancellationToken)
				.ConfigureAwait(false);
			db.SharedSecretReferences.RemoveRange(references);

			var grants = await db.SharedSecretAppGrants
				.Where(g => g.SharedSecretId == id)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			db.SharedSecretAppGrants.RemoveRange(grants);

			db.SharedSecrets.Remove(locked);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { name = locked.Name, detachedReferenceCount = references.Count },
				AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.SharedSecretDeleted, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}

	public async Task GrantAsync(
		Guid sharedSecretId, Guid appId, string? actorUserId, CancellationToken cancellationToken = default)
	{
		var exists = await db.SharedSecretAppGrants.AsNoTracking()
			.AnyAsync(g => g.SharedSecretId == sharedSecretId && g.AppId == appId, cancellationToken)
			.ConfigureAwait(false);
		if (exists)
		{
			return;
		}

		db.SharedSecretAppGrants.Add(new SharedSecretAppGrant
		{
			Id = Guid.NewGuid(),
			SharedSecretId = sharedSecretId,
			AppId = appId,
			GrantedAt = DateTimeOffset.UtcNow,
			GrantedByUserId = actorUserId,
		});
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		var details = System.Text.Json.JsonSerializer.Serialize(
			new { sharedSecretId }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.SharedSecretGrantAdded, actorUserId, appId, details, cancellationToken)
			.ConfigureAwait(false);
	}

	public async Task RevokeGrantAsync(
		Guid sharedSecretId, Guid appId, string? actorUserId, CancellationToken cancellationToken = default)
	{
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			// DeleteAsync와 대칭 - 이 App이 이미 가진 참조를 먼저 전부 detach한다("그랜트 없음 +
			// 참조는 있음"이라는 반쪽 상태를 남기지 않기 위함). 값은 이미 그 App 번들에
			// materialize돼 있으므로 참조 행만 지우면 자체 소유 키로 전환된다.
			var secret = await db.SharedSecrets.AsNoTracking()
				.SingleAsync(s => s.Id == sharedSecretId, cancellationToken).ConfigureAwait(false);
			var references = await db.SharedSecretReferences
				.Where(r => r.SharedSecretId == sharedSecretId && r.Env!.AppId == appId)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
			await PromoteExpirationsForReferencesAsync(secret, references, cancellationToken)
				.ConfigureAwait(false);
			db.SharedSecretReferences.RemoveRange(references);

			var grant = await db.SharedSecretAppGrants
				.SingleOrDefaultAsync(
					g => g.SharedSecretId == sharedSecretId && g.AppId == appId, cancellationToken)
				.ConfigureAwait(false);
			if (grant is not null)
			{
				db.SharedSecretAppGrants.Remove(grant);
			}

			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { sharedSecretId, detachedReferenceCount = references.Count }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.SharedSecretGrantRevoked, actorUserId, appId, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}

	// detach되는 참조가 SharedSecret의 만료일을 잃지 않도록, 그 Env에 아직 자체 만료일이 없는
	// 경우에만 KeyExpiration으로 승격 복제한다(이미 자체 만료일이 있으면 사용자 의도를 존중해
	// 덮어쓰지 않음).
	private async Task PromoteExpirationsForReferencesAsync(
		SharedSecret secret, IReadOnlyList<SharedSecretReference> references,
		CancellationToken cancellationToken)
	{
		if (secret.ExpiresAt is null || references.Count == 0)
		{
			return;
		}

		foreach (var reference in references)
		{
			var hasOwnExpiration = await db.KeyExpirations.AnyAsync(
				k => k.EnvId == reference.EnvId && k.IsOverwriteBundle == reference.IsOverwriteBundle
					&& k.KeyName == reference.KeyName,
				cancellationToken).ConfigureAwait(false);
			if (hasOwnExpiration)
			{
				continue;
			}

			db.KeyExpirations.Add(new KeyExpiration
			{
				Id = Guid.NewGuid(),
				EnvId = reference.EnvId,
				IsOverwriteBundle = reference.IsOverwriteBundle,
				KeyName = reference.KeyName,
				ExpiresAt = secret.ExpiresAt.Value,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}
	}

	public async Task<IReadOnlyList<Guid>> ListGrantedAppIdsAsync(
		Guid sharedSecretId, CancellationToken cancellationToken = default)
	{
		return await db.SharedSecretAppGrants.AsNoTracking()
			.Where(g => g.SharedSecretId == sharedSecretId)
			.Select(g => g.AppId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<SharedSecretReferenceInfo>> ListReferencesAsync(
		Guid sharedSecretId, CancellationToken cancellationToken = default)
	{
		var rows = await db.SharedSecretReferences.AsNoTracking()
			.Where(r => r.SharedSecretId == sharedSecretId)
			.Join(db.Envs.AsNoTracking(), r => r.EnvId, e => e.Id, (r, e) => new { r, e })
			.Join(db.Apps.AsNoTracking(), re => re.e.AppId, a => a.Id, (re, a) => new
			{
				re.r.EnvId, AppName = a.Name, EnvName = re.e.Name, re.r.IsOverwriteBundle, re.r.KeyName,
				re.r.LastMaterializedAt,
			})
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		// EnvName.ToObjectSegment()는 확장 메서드라 SQL로 번역되지 않으므로 목록을 다 가져온 뒤
		// 클라이언트 측에서 적용한다.
		return rows.Select(r => new SharedSecretReferenceInfo(
			r.EnvId, r.AppName, r.EnvName.ToObjectSegment(), r.IsOverwriteBundle, r.KeyName,
			r.LastMaterializedAt))
			.ToList();
	}
}

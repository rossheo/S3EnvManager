using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class SharedSecretService(
	ApplicationDbContext db, IAppSecretKeyCipher cipher, IAuditLogger auditLogger)
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
		var failures = new List<SharedSecretCascadeFailure>();

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			// 같은 SharedSecret에 대한 동시 갱신이 뒤섞이지 않도록 행을 잠근다 - cascade
			// 재materialize(Phase 4)까지 이 잠금 아래에서 실행된다.
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

				// Phase 4에서 여기에 참조하는 모든 (EnvId, IsOverwriteBundle)를 순회하며
				// ISecretBundleService.SaveAsync로 재materialize하는 cascade를 채운다. 아직
				// SharedSecretReference를 만드는 경로(Phase 3)가 없으므로 지금은 항상 빈 목록이다.
				var references = await db.SharedSecretReferences.AsNoTracking()
					.Where(r => r.SharedSecretId == id)
					.ToListAsync(cancellationToken).ConfigureAwait(false);
				_ = references; // Phase 4에서 실제 cascade 로직으로 대체.
			}

			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { valueChanged = newValue is not null }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.SharedSecretUpdated, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);

		return new SharedSecretUpdateResult(failures);
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
			// 자체 소유 키가 된다) - 절대 dangling 상태를 남기지 않는다. Phase 5에서 여기에
			// SharedSecret의 만료일을 각 Env의 KeyExpiration으로 승격 복제하는 단계를 추가한다.
			var references = await db.SharedSecretReferences
				.Where(r => r.SharedSecretId == id)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
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
			var references = await db.SharedSecretReferences
				.Where(r => r.SharedSecretId == sharedSecretId && r.Env!.AppId == appId)
				.ToListAsync(cancellationToken).ConfigureAwait(false);
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

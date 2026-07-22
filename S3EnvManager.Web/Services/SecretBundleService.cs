using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// kms는 admin role(GenerateDataKey/Encrypt/Decrypt), appKms는 app role(Encrypt만)로 동작한다 -
// 두 부트스트랩 identity 분리.
public sealed class SecretBundleService(
	ApplicationDbContext db,
	ISecretObjectStore store,
	IKmsKeyOperations kms,
	[FromKeyedServices(CmkRole.App)] IKmsKeyOperations appKms,
	IAuditLogger auditLogger,
	IPrimaryStorageSettingsStore primaryStorageSettingsStore,
	IMemoryCache cache)
	: ISecretBundleService
{
	private static readonly TimeSpan KeyCountCacheDuration = TimeSpan.FromMinutes(10);

	private static string KeyCountCacheKey(string key) => $"secret-key-count:{key}";

	public async Task<SecretEditSession> LoadForEditAsync(
		Guid envId, SecretBundleKind kind = SecretBundleKind.Base, CancellationToken cancellationToken = default)
	{
		var env = await db.Envs.Include(e => e.App).AsNoTracking()
			.SingleAsync(e => e.Id == envId, cancellationToken).ConfigureAwait(false);
		var (bucket, key) = await ObjectLocationAsync(env, kind, cancellationToken).ConfigureAwait(false);

		var stored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		if (stored is null)
		{
			return new SecretEditSession(new Dictionary<string, string>(), null);
		}

		var values = await SopsEnvelopeCodec.DecryptAsAdminAsync(stored.Content, kms, cancellationToken)
			.ConfigureAwait(false);
		return new SecretEditSession(values, stored.ETag);
	}

	public async Task<SaveOutcome> SaveAsync(
		Guid envId,
		IReadOnlyDictionary<string, string> baseSnapshot,
		string? baseETag,
		IReadOnlyDictionary<string, string> editedValues,
		string? actorUserId = null,
		string? actorEmail = null,
		SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default)
	{
		// Blazor Server는 클라이언트가 보낸 문자열을 그대로 바인딩하므로 화면 입력 제약과 무관하게
		// 서버 사이드에서 반드시 다시 검증해야 한다.
		foreach (var editedKey in editedValues.Keys)
		{
			var keyError = SecretKeyNameValidator.Validate(editedKey);
			if (keyError is not null)
			{
				return new SaveFailed($"키 이름이 올바르지 않습니다: {keyError}");
			}
		}

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			// base ETag와 DB의 last_known_etag 비교를 행 잠금으로 감싸 비교-후-쓰기를 직렬화한다.
			var lockedEnv = await db.Envs
				.FromSqlInterpolated($"SELECT * FROM \"Envs\" WHERE \"Id\" = {envId} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);
			var app = await db.Apps.AsNoTracking()
				.SingleAsync(a => a.Id == lockedEnv.AppId, cancellationToken).ConfigureAwait(false);
			var (bucket, key) = await ObjectLocationAsync(app, lockedEnv, kind, cancellationToken)
				.ConfigureAwait(false);

			// base/overwrite는 서로 다른 S3 오브젝트라 ETag를 독립적으로 추적한다.
			var trackedETag = kind == SecretBundleKind.Base
				? lockedEnv.LastKnownETag
				: lockedEnv.OverwriteLastKnownETag;
			if (trackedETag != baseETag)
			{
				return await BuildConflictAsync(bucket, key, baseSnapshot, editedValues, cancellationToken)
					.ConfigureAwait(false);
			}

			var adminArn = await GetActiveCmkArnAsync(CmkRole.Admin, cancellationToken).ConfigureAwait(false);
			var appArn = await GetActiveCmkArnAsync(CmkRole.App, cancellationToken).ConfigureAwait(false);

			// 검증 실패 시 되돌릴 직전 버전을 쓰기 전에 미리 잡아둔다.
			var previous = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);

			var encryptedContent = await SopsEnvelopeCodec.EncryptAsync(
				editedValues, adminArn, appArn, app.Name, kms, appKms, cancellationToken)
				.ConfigureAwait(false);
			var putResult = await store.PutAsync(bucket, key, encryptedContent, actorEmail, cancellationToken)
				.ConfigureAwait(false);

			// 복호화 실패 등 검증 자체 예외도 값 불일치와 동일하게 취급해 롤백한다.
			var verificationPassed = false;
			try
			{
				var verifyStored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false)
					?? throw new InvalidOperationException("방금 저장한 오브젝트를 다시 읽을 수 없습니다.");
				var verifiedValues = await SopsEnvelopeCodec.DecryptAsAdminAsync(
					verifyStored.Content, kms, cancellationToken)
					.ConfigureAwait(false);
				verificationPassed = ValuesEqual(verifiedValues, editedValues);
			}
			catch
			{
				verificationPassed = false;
			}

			if (!verificationPassed)
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

				if (previous is null)
				{
					await store.DeleteAsync(bucket, key, cancellationToken).ConfigureAwait(false);
					return new SaveFailed("저장 검증에 실패했습니다. 다시 시도해 주세요.");
				}

				if (previous.VersionId is not null)
				{
					await store.RestoreVersionAsync(bucket, key, previous.VersionId, cancellationToken)
						.ConfigureAwait(false);
					return new SaveFailed("저장 검증에 실패해 이전 버전으로 되돌렸습니다. 다시 시도해 주세요.");
				}

				// VersionId가 없다는 것은 버킷 버저닝이 꺼져 있다는 뜻이다(자가 치유가 정상
				// 동작했다면 있을 수 없는 상태) - 되돌릴 방법이 없으므로 오류로만 알린다.
				return new SaveFailed("저장 검증에 실패했지만 버킷 버저닝이 꺼져 있어 자동으로 되돌릴 수 없습니다. " +
					"버킷 상태를 확인하세요.");
			}

			if (kind == SecretBundleKind.Base)
			{
				lockedEnv.LastKnownETag = putResult.ETag;
			}
			else
			{
				lockedEnv.OverwriteLastKnownETag = putResult.ETag;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			// 값 자체는 시크릿이므로 절대 로그에 남기지 않는다.
			var diff = DescribeKeyDiff(baseSnapshot, editedValues);
			if (diff is not null)
			{
				var eventType = kind == SecretBundleKind.Base
					? AuditEventTypes.SecretEdited
					: AuditEventTypes.OverwriteSecretEdited;
				await auditLogger.LogAsync(eventType, actorUserId, app.Id, diff, cancellationToken).ConfigureAwait(false);
			}

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

			cache.Remove(KeyCountCacheKey(key));

			return new SaveSuccess(putResult.ETag) as SaveOutcome;
		}).ConfigureAwait(false);
	}

	public async Task<IReadOnlyList<SecretObjectVersion>> ListHistoryAsync(
		Guid envId, SecretBundleKind kind = SecretBundleKind.Base, CancellationToken cancellationToken = default)
	{
		var env = await db.Envs.Include(e => e.App).AsNoTracking()
			.SingleAsync(e => e.Id == envId, cancellationToken).ConfigureAwait(false);
		var (bucket, key) = await ObjectLocationAsync(env, kind, cancellationToken).ConfigureAwait(false);

		var versions = await store.ListVersionsAsync(
			bucket, key, includeActorEmail: true, cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return versions.OrderByDescending(v => v.LastModified).ToList();
	}

	public async Task<IReadOnlyDictionary<string, string>> LoadVersionAsync(
		Guid envId, string versionId, SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default)
	{
		var env = await db.Envs.Include(e => e.App).AsNoTracking()
			.SingleAsync(e => e.Id == envId, cancellationToken).ConfigureAwait(false);
		var (bucket, key) = await ObjectLocationAsync(env, kind, cancellationToken).ConfigureAwait(false);

		var content = await store.GetVersionContentAsync(bucket, key, versionId, cancellationToken)
			.ConfigureAwait(false);
		return await SopsEnvelopeCodec.DecryptAsAdminAsync(content, kms, cancellationToken).ConfigureAwait(false);
	}

	public async Task<Int32> GetKeyCountAsync(
		App app, Env env, SecretBundleKind kind = SecretBundleKind.Base,
		CancellationToken cancellationToken = default)
	{
		var key = SecretObjectKeys.Locate(app, env, kind);
		var cacheKey = KeyCountCacheKey(key);
		if (cache.TryGetValue(cacheKey, out Int32 cachedCount))
		{
			return cachedCount;
		}

		var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);
		var stored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		var count = stored is null ? 0 : SopsDotEnvDocument.Parse(stored.Content).Entries.Count;
		// App x Env 개수만큼 반복 호출되므로 짧게 캐싱한다 - SaveAsync가 저장 시 명시적으로 무효화한다.
		cache.Set(cacheKey, count, KeyCountCacheDuration);
		return count;
	}

	private async Task<SaveConflict> BuildConflictAsync(
		string bucket,
		string key,
		IReadOnlyDictionary<string, string> baseSnapshot,
		IReadOnlyDictionary<string, string> mineValues,
		CancellationToken cancellationToken)
	{
		var theirsStored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		var theirsValues = theirsStored is null
			? new Dictionary<string, string>()
			: await SopsEnvelopeCodec.DecryptAsAdminAsync(theirsStored.Content, kms, cancellationToken)
				.ConfigureAwait(false);

		var report = SecretMerge.Merge(baseSnapshot, mineValues, theirsValues);
		return new SaveConflict(
			report.MergedValues, report.AutoAppliedTheirsKeys, report.RealConflicts, theirsValues,
			theirsStored?.ETag);
	}

	private async Task<string> GetActiveCmkArnAsync(CmkRole role, CancellationToken cancellationToken)
	{
		var arn = await db.CmkRegistrations.AsNoTracking()
			.Where(c => c.Role == role && c.Status == CmkStatus.Active)
			.Select(c => c.Arn)
			.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		return arn ?? throw new InvalidOperationException(
			$"{role} role의 활성 CMK가 등록되어 있지 않습니다. 관리자가 먼저 CMK를 등록해야 합니다.");
	}

	private async Task<string> GetPrimaryBucketAsync(CancellationToken cancellationToken)
	{
		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false);
		return bucket ?? throw new InvalidOperationException("주 저장소가 아직 프로비저닝되지 않았습니다.");
	}

	private async Task<(string Bucket, string Key)> ObjectLocationAsync(
		Env env, SecretBundleKind kind, CancellationToken cancellationToken)
	{
		var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);
		return (bucket, SecretObjectKeys.Locate(env.App!, env, kind));
	}

	private async Task<(string Bucket, string Key)> ObjectLocationAsync(
		App app, Env env, SecretBundleKind kind, CancellationToken cancellationToken)
	{
		var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);
		return (bucket, SecretObjectKeys.Locate(app, env, kind));
	}

	// 추가/변경/삭제된 키 이름만 담는다(값은 절대 포함하지 않음). 아무 키도 안 바뀌었으면 null.
	private static string? DescribeKeyDiff(
		IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> after)
	{
		var added = after.Keys.Where(k => !before.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
		var removed = before.Keys.Where(k => !after.ContainsKey(k))
			.OrderBy(k => k, StringComparer.Ordinal).ToList();
		var changed = after.Keys.Where(k => before.TryGetValue(k, out var v) && v != after[k])
			.OrderBy(k => k, StringComparer.Ordinal).ToList();

		if (added.Count == 0 && removed.Count == 0 && changed.Count == 0)
		{
			return null;
		}

		return System.Text.Json.JsonSerializer.Serialize(new { added, changed, removed }, AuditJsonOptions.Default);
	}

	private static bool ValuesEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		foreach (var (key, value) in a)
		{
			if (!b.TryGetValue(key, out var otherValue) || otherValue != value)
			{
				return false;
			}
		}
		return true;
	}
}
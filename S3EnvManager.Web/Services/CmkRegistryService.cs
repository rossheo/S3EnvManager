using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class CmkRegistryService(
	ApplicationDbContext db, IAuditLogger auditLogger, IAppCredentialProvisioner appCredentialProvisioner,
	ISecretObjectStore store, IKmsKeyOperations kms,
	IBootstrapAppIdentityProvisioner bootstrapAppIdentityProvisioner,
	IPrimaryStorageSettingsStore primaryStorageSettingsStore,
	IKmsKeyAdministration kmsKeyAdministration) : ICmkRegistryService
{
	private static readonly IReadOnlyDictionary<string, string> NoContext = new Dictionary<string, string>();
	private static readonly SecretBundleKind[] AllKinds = [SecretBundleKind.Base, SecretBundleKind.Overwrite];

	public Task<List<CmkRegistration>> ListAsync(CancellationToken cancellationToken = default) =>
		db.CmkRegistrations.AsNoTracking()
			.OrderBy(c => c.Role).ThenBy(c => c.CreatedAt)
			.ToListAsync(cancellationToken);

	public async Task<CmkRegistration> RegisterAsync(
		CmkRole role, string arn, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var arnError = CmkArnValidator.Validate(arn);
		if (arnError is not null)
		{
			throw new ArgumentException(arnError, nameof(arn));
		}

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		var registration = await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			// 같은 CMK를 admin/app 두 role에 걸쳐 등록하면 한쪽 role의 IAM 사용자가 다른 role의
			// 엔트리까지 복호화할 수 있게 되어 role 경계가 무너진다 - 무조건 막는다.
			var existingByArn = await db.CmkRegistrations
				.FromSqlInterpolated($"SELECT * FROM \"CmkRegistrations\" WHERE \"Arn\" = {arn} FOR UPDATE")
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			var conflictingRole = existingByArn.FirstOrDefault(c => c.Role != role);
			if (conflictingRole is not null)
			{
				throw new InvalidOperationException(
					$"이 CMK ARN은 이미 {conflictingRole.Role} role로 등록되어 있습니다. " +
					"admin/app이 같은 CMK를 공유하면 두 role의 복호화 권한 경계가 무너지므로, 서로 다른 CMK를 쓰세요.");
			}

			if (existingByArn.Any(c => c.Role == role))
			{
				throw new InvalidOperationException($"이미 등록된 CMK ARN입니다: {arn}");
			}

			var existingInRole = await db.CmkRegistrations
				.FromSqlInterpolated($"SELECT * FROM \"CmkRegistrations\" WHERE \"Role\" = {role} FOR UPDATE")
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			// role에 등록된 CMK가 하나도 없으면 최초 등록이 자동으로 활성이 된다 - 각 role에는
			// 항상 최소 1개의 활성 CMK가 있어야 한다.
			var status = existingInRole.Count == 0 ? CmkStatus.Active : CmkStatus.Secondary;

			var registration = new CmkRegistration
			{
				CmkId = Guid.NewGuid(),
				Arn = arn,
				Role = role,
				Status = status,
				CreatedAt = DateTimeOffset.UtcNow,
			};
			db.CmkRegistrations.Add(registration);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			// 태그가 없으면 admin 정책의 태그 조건에 들어오지 못해 Encrypt/Decrypt/GenerateDataKey가
			// 전부 AccessDenied로 실패한다 - 태깅 실패 시 쓸 수 없는 CMK를 남기지 않도록 등록을 되돌린다.
			try
			{
				await kmsKeyAdministration.TagKeyAsync(arn, KmsAliasConventions.ManagedTag, cancellationToken)
					.ConfigureAwait(false);
			}
			catch
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				throw;
			}

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { role = role.ToString(), arn, status = status.ToString() }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.CmkRegistered, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return registration;
		}).ConfigureAwait(false);

		if (role == CmkRole.App)
		{
			await ReapplyAppRoleGrantsToAllAppsAsync(cancellationToken).ConfigureAwait(false);
		}

		return registration;
	}

	public async Task PromoteAsync(
		Guid cmkId, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		var (target, promoted) = await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var target = await db.CmkRegistrations
				.FromSqlInterpolated($"SELECT * FROM \"CmkRegistrations\" WHERE \"CmkId\" = {cmkId} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);

			if (target.Status == CmkStatus.Active)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return (target, false);
			}

			var roleRegistrations = await db.CmkRegistrations
				.FromSqlInterpolated($"SELECT * FROM \"CmkRegistrations\" WHERE \"Role\" = {target.Role} FOR UPDATE")
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			var previousActive = roleRegistrations.SingleOrDefault(c => c.Status == CmkStatus.Active);
			if (previousActive is not null)
			{
				previousActive.Status = CmkStatus.Secondary;
			}
			target.Status = CmkStatus.Active;
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(new
			{
				role = target.Role.ToString(),
				promotedArn = target.Arn,
				demotedArn = previousActive?.Arn,
			}, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.CmkPromoted, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return (target, true);
		}).ConfigureAwait(false);

		if (promoted && target.Role == CmkRole.App)
		{
			await ReapplyAppRoleGrantsToAllAppsAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	public async Task RemoveAsync(
		Guid cmkId, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var target = await db.CmkRegistrations.AsNoTracking()
			.SingleAsync(c => c.CmkId == cmkId, cancellationToken).ConfigureAwait(false);

		if (target.Status == CmkStatus.Active)
		{
			throw new InvalidOperationException("활성 CMK는 제거할 수 없습니다. 먼저 다른 CMK를 활성으로 승격하세요.");
		}

		if (target.Role == CmkRole.App)
		{
			await RemoveAppRoleCmkAsync(target, actorUserId, cancellationToken).ConfigureAwait(false);
		}
		else
		{
			await RemoveAdminRoleCmkAsync(target, actorUserId, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task RemoveAppRoleCmkAsync(
		CmkRegistration target, string? actorUserId, CancellationToken cancellationToken)
	{
		var activeAppCmk = await db.CmkRegistrations.AsNoTracking()
			.SingleAsync(c => c.Role == CmkRole.App && c.Status == CmkStatus.Active, cancellationToken)
			.ConfigureAwait(false);

		// 소프트 삭제된 App은 재래핑/의존 대상 집계에서 제외한다 - 그러지 않으면 소프트 삭제된 App이
		// 남아있는 한 이 CMK를 영원히 제거할 수 없다.
		var apps = await db.Apps.Include(a => a.Envs).AsNoTracking()
			.Where(a => a.DeletedAt == null)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);

		foreach (var app in apps)
		{
			foreach (var env in app.Envs)
			{
				foreach (var kind in AllKinds)
				{
					await RewrapEntryIfWrappedByAsync(
						bucket, app, env, kind, entryIndex: 1, target.Arn, activeAppCmk.Arn, cancellationToken)
						.ConfigureAwait(false);
				}
			}
		}

		// 재래핑 이후에도 여전히 참조가 남아 있으면(동시 편집 등) 제거를 중단한다 - 재시도는 안전하다.
		var remainingDependents = 0;
		foreach (var app in apps)
		{
			foreach (var env in app.Envs)
			{
				foreach (var kind in AllKinds)
				{
					var arn = await GetCurrentEntryArnAsync(bucket, app, env, kind, entryIndex: 1, cancellationToken)
						.ConfigureAwait(false);
					if (arn == target.Arn)
					{
						remainingDependents++;
					}
				}
			}
		}
		if (remainingDependents > 0)
		{
			throw new InvalidOperationException(
				$"재래핑 후에도 이 CMK를 참조하는 대상이 {remainingDependents}개 남아 있어 제거를 중단했습니다. 다시 시도하세요.");
		}

		await RemoveRegistrationAsync(target, actorUserId,
			new { role = "App", removedArn = target.Arn, rewrappedInto = activeAppCmk.Arn },
			cancellationToken).ConfigureAwait(false);

		await ReapplyAppRoleGrantsToAllAppsAsync(cancellationToken).ConfigureAwait(false);
	}

	private async Task RemoveAdminRoleCmkAsync(
		CmkRegistration target, string? actorUserId, CancellationToken cancellationToken)
	{
		var activeAdminCmk = await db.CmkRegistrations.AsNoTracking()
			.SingleAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active, cancellationToken)
			.ConfigureAwait(false);

		var apps = await db.Apps.Include(a => a.Envs).AsNoTracking()
			.Where(a => a.DeletedAt == null)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);

		// 1) 현재 버전의 admin 엔트리를 재래핑한다.
		foreach (var app in apps)
		{
			foreach (var env in app.Envs)
			{
				foreach (var kind in AllKinds)
				{
					await RewrapEntryIfWrappedByAsync(
						bucket, app, env, kind, entryIndex: 0, target.Arn, activeAdminCmk.Arn, cancellationToken)
						.ConfigureAwait(false);
				}
			}
		}

		// 2) noncurrent 버전은 S3 버전 불변성상 제자리 재래핑이 불가능하므로 삭제한다(파괴적).
		var deletedVersionCount = 0;
		foreach (var app in apps)
		{
			foreach (var env in app.Envs)
			{
				foreach (var kind in AllKinds)
				{
					deletedVersionCount += await DeleteNoncurrentVersionsWrappedByAsync(
						bucket, app, env, kind, target.Arn, cancellationToken)
						.ConfigureAwait(false);
				}
			}
		}

		// 3) 데이터 키 세대 테이블도 재래핑한다.
		var rewrappedGenerationCount = await RewrapDataKeyGenerationsAsync(
			target.CmkId, target.Arn, activeAdminCmk.CmkId, activeAdminCmk.Arn, cancellationToken)
			.ConfigureAwait(false);

		// 재확인: 현재 버전/noncurrent 버전/DataKeyGeneration 전부 의존 대상이 0개인지 확인한다.
		var remainingDependents = 0;
		foreach (var app in apps)
		{
			foreach (var env in app.Envs)
			{
				foreach (var kind in AllKinds)
				{
					var currentArn = await GetCurrentEntryArnAsync(bucket, app, env, kind, entryIndex: 0, cancellationToken)
						.ConfigureAwait(false);
					if (currentArn == target.Arn)
					{
						remainingDependents++;
					}

					var key = SecretObjectKeys.Locate(app, env, kind);
					var versions = await store.ListVersionsAsync(bucket, key, cancellationToken: cancellationToken)
						.ConfigureAwait(false);
					foreach (var version in versions.Where(v => !v.IsLatest))
					{
						var arn = await TryGetEntryArnAtVersionAsync(
							bucket, key, version.VersionId, entryIndex: 0, cancellationToken)
							.ConfigureAwait(false);
						if (arn == target.Arn)
						{
							remainingDependents++;
						}
					}
				}
			}
		}
		var remainingGenerations = await db.DataKeyGenerations.AsNoTracking()
			.CountAsync(g => g.CmkId == target.CmkId, cancellationToken)
			.ConfigureAwait(false);
		if (remainingDependents > 0 || remainingGenerations > 0)
		{
			throw new InvalidOperationException(
				$"재래핑/삭제 후에도 이 CMK를 참조하는 대상이 남아 있어({remainingDependents}개 번들, {remainingGenerations}개 데이터 키 세대) " +
				"제거를 중단했습니다. 다시 시도하세요.");
		}

		await RemoveRegistrationAsync(target, actorUserId, new
		{
			role = "Admin",
			removedArn = target.Arn,
			rewrappedInto = activeAdminCmk.Arn,
			deletedNoncurrentVersions = deletedVersionCount,
			rewrappedDataKeyGenerations = rewrappedGenerationCount,
		}, cancellationToken).ConfigureAwait(false);

		// admin CMK는 App IAM 정책과 무관하므로 재적용할 정책이 없다.
	}

	private async Task RemoveRegistrationAsync(
		CmkRegistration target, string? actorUserId, object auditDetails, CancellationToken cancellationToken)
	{
		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);
			var lockedTarget = await db.CmkRegistrations
				.FromSqlInterpolated($"SELECT * FROM \"CmkRegistrations\" WHERE \"CmkId\" = {target.CmkId} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);
			if (lockedTarget.Status == CmkStatus.Active)
			{
				throw new InvalidOperationException("재래핑 도중 이 CMK가 활성으로 승격됐습니다. 제거를 중단했습니다.");
			}
			db.CmkRegistrations.Remove(lockedTarget);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(auditDetails, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.CmkRemoved, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}

	// entryIndex(0=admin, 1=app) 엔트리만 재래핑한다 - 암호화된 값/다른 엔트리/MAC은 건드리지 않는다.
	private async Task RewrapEntryIfWrappedByAsync(
		string bucket, App app, Env env, SecretBundleKind kind, Int32 entryIndex, string cmkToRemoveArn,
		string targetArn, CancellationToken cancellationToken)
	{
		var key = SecretObjectKeys.Locate(app, env, kind);

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);
			var lockedEnv = await db.Envs
				.FromSqlInterpolated($"SELECT * FROM \"Envs\" WHERE \"Id\" = {env.Id} FOR UPDATE")
				.SingleAsync(cancellationToken).ConfigureAwait(false);

			var stored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
			if (stored is null)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return;
			}

			var document = SopsDotEnvDocument.Parse(stored.Content);
			if (document.KmsEntries.Count <= entryIndex || document.KmsEntries[entryIndex].Arn != cmkToRemoveArn)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return;
			}

			var entry = document.KmsEntries[entryIndex];
			var dataKey = await kms.DecryptAsync(
				cmkToRemoveArn, entry.CiphertextBlob, entry.EncryptionContext, cancellationToken)
				.ConfigureAwait(false);
			var newCiphertext = await kms.EncryptAsync(targetArn, dataKey, entry.EncryptionContext, cancellationToken)
				.ConfigureAwait(false);
			document.KmsEntries[entryIndex] = new SopsKmsEntry(
				targetArn, newCiphertext, DateTimeOffset.UtcNow, entry.EncryptionContext);

			// LastModified/EncryptedMac은 절대 건드리지 않는다 - MAC이 LastModified를 AAD로
			// 쓰므로, 바꾸면 이후 모든 복호화가 MAC 검증에 실패한다.
			var newContent = document.Serialize();
			var putResult = await store.PutAsync(bucket, key, newContent, cancellationToken: cancellationToken)
				.ConfigureAwait(false);

			if (kind == SecretBundleKind.Base)
			{
				lockedEnv.LastKnownETag = putResult.ETag;
			}
			else
			{
				lockedEnv.OverwriteLastKnownETag = putResult.ETag;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}

	// S3 버전은 불변이라 제자리 재래핑이 불가능하므로 이 CMK를 참조하는 noncurrent 버전을 삭제한다
	// (파괴적, 되돌릴 수 없음).
	private async Task<Int32> DeleteNoncurrentVersionsWrappedByAsync(
		string bucket, App app, Env env, SecretBundleKind kind, string cmkToRemoveArn,
		CancellationToken cancellationToken)
	{
		var key = SecretObjectKeys.Locate(app, env, kind);
		var versions = await store.ListVersionsAsync(bucket, key, cancellationToken: cancellationToken)
			.ConfigureAwait(false);

		var deletedCount = 0;
		foreach (var version in versions.Where(v => !v.IsLatest))
		{
			var arn = await TryGetEntryArnAtVersionAsync(
				bucket, key, version.VersionId, entryIndex: 0, cancellationToken).ConfigureAwait(false);
			if (arn == cmkToRemoveArn)
			{
				await store.DeleteVersionAsync(bucket, key, version.VersionId, cancellationToken).ConfigureAwait(false);
				deletedCount++;
			}
		}
		return deletedCount;
	}

	private async Task<string?> TryGetEntryArnAtVersionAsync(
		string bucket, string key, string versionId, Int32 entryIndex, CancellationToken cancellationToken)
	{
		try
		{
			var content = await store.GetVersionContentAsync(bucket, key, versionId, cancellationToken)
				.ConfigureAwait(false);
			var document = SopsDotEnvDocument.Parse(content);
			return document.KmsEntries.Count <= entryIndex ? null : document.KmsEntries[entryIndex].Arn;
		}
		catch
		{
			return null;
		}
	}

	private async Task<Int32> RewrapDataKeyGenerationsAsync(
		Guid cmkToRemoveId, string cmkToRemoveArn, Guid targetCmkId, string targetArn,
		CancellationToken cancellationToken)
	{
		var generations = await db.DataKeyGenerations.Where(g => g.CmkId == cmkToRemoveId)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		foreach (var generation in generations)
		{
			var dataKey = await kms.DecryptAsync(
				cmkToRemoveArn, generation.CiphertextBlob, NoContext, cancellationToken)
				.ConfigureAwait(false);
			var newCiphertext = await kms.EncryptAsync(targetArn, dataKey, NoContext, cancellationToken)
				.ConfigureAwait(false);
			generation.CiphertextBlob = newCiphertext;
			generation.CmkId = targetCmkId;
		}
		if (generations.Count > 0)
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
		return generations.Count;
	}

	private async Task<string?> GetCurrentEntryArnAsync(
		string bucket, App app, Env env, SecretBundleKind kind, Int32 entryIndex,
		CancellationToken cancellationToken)
	{
		var key = SecretObjectKeys.Locate(app, env, kind);
		var stored = await store.GetCurrentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		if (stored is null)
		{
			return null;
		}

		var document = SopsDotEnvDocument.Parse(stored.Content);
		return document.KmsEntries.Count <= entryIndex ? null : document.KmsEntries[entryIndex].Arn;
	}

	private async Task<string> GetPrimaryBucketAsync(CancellationToken cancellationToken)
	{
		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false);
		return bucket ?? throw new InvalidOperationException("주 저장소가 아직 프로비저닝되지 않았습니다.");
	}

	// 활성 CMK 하나만 부여하면 승격 이전에 발급된 자격증명이 이후 시크릿을 못 읽게 되므로,
	// 등록된 app role CMK 전부로 모든 App(+ 부트스트랩 app identity)의 정책을 다시 맞춘다.
	private async Task ReapplyAppRoleGrantsToAllAppsAsync(CancellationToken cancellationToken)
	{
		var appCmkArns = await db.CmkRegistrations.AsNoTracking()
			.Where(c => c.Role == CmkRole.App)
			.Select(c => c.Arn)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		var apps = await db.Apps.AsNoTracking()
			.Where(a => a.DeletedAt == null)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		if (apps.Count > 0)
		{
			var bucket = await GetPrimaryBucketAsync(cancellationToken).ConfigureAwait(false);
			foreach (var app in apps)
			{
				await appCredentialProvisioner.ReapplyPolicyAsync(app.Name, bucket, appCmkArns, cancellationToken)
					.ConfigureAwait(false);
			}
		}

		await bootstrapAppIdentityProvisioner.TryPutPolicyIfProvisionedAsync(appCmkArns, cancellationToken)
			.ConfigureAwait(false);
	}
}
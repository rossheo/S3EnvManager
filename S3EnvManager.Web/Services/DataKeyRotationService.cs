using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public enum DataKeyRotationOutcome
{
	NoGenerationsYet,
	NotDueYet,
	Rotated,
}

/// <summary>여러 인스턴스가 동시에 트리거해도 중복 세대가 생기지 않도록, 싱글턴 설정 행을
/// 잠금 삼아 확인 후 발급을 직렬화한다.</summary>
public static class DataKeyRotationService
{
	private static readonly IReadOnlyDictionary<string, string> NoContext = new Dictionary<string, string>();

	public static Task<DataKeyRotationOutcome> RotateIfDueAsync(
		ApplicationDbContext db, IKmsKeyOperations kms, IAuditLogger auditLogger, TimeProvider timeProvider,
		CancellationToken cancellationToken = default) =>
		RotateAsync(db, kms, auditLogger, timeProvider, force: false, actorUserId: null, cancellationToken);

	public static Task<DataKeyRotationOutcome> RotateNowAsync(
		ApplicationDbContext db, IKmsKeyOperations kms, IAuditLogger auditLogger, TimeProvider timeProvider,
		string? actorUserId, CancellationToken cancellationToken = default) =>
		RotateAsync(db, kms, auditLogger, timeProvider, force: true, actorUserId, cancellationToken);

	private static async Task<DataKeyRotationOutcome> RotateAsync(
		ApplicationDbContext db, IKmsKeyOperations kms, IAuditLogger auditLogger, TimeProvider timeProvider,
		bool force, string? actorUserId, CancellationToken cancellationToken)
	{
		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		return await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var settings = await db.DataKeyRotationSettings
				.FromSqlInterpolated($"""
					SELECT * FROM "DataKeyRotationSettings"
					WHERE "Id" = {DataKeyRotationSettings.SingletonId} FOR UPDATE
					""")
				.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
			if (settings is null)
			{
				settings = new DataKeyRotationSettings
				{
					RotationIntervalDays = IDataKeyRotationSettingsService.DefaultDays,
					UpdatedAt = timeProvider.GetUtcNow(),
				};
				db.DataKeyRotationSettings.Add(settings);
				await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			}
			var intervalDays = settings.RotationIntervalDays;

			var latest = await db.DataKeyGenerations.AsNoTracking()
				.OrderByDescending(d => d.CreatedAt)
				.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
			if (latest is null)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return DataKeyRotationOutcome.NoGenerationsYet;
			}

			var due = force || timeProvider.GetUtcNow() >= latest.CreatedAt + TimeSpan.FromDays(intervalDays);
			if (!due)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return DataKeyRotationOutcome.NotDueYet;
			}

			var adminCmk = await db.CmkRegistrations.AsNoTracking()
				.SingleAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active, cancellationToken)
				.ConfigureAwait(false);
			var (_, ciphertextBlob) = await kms.GenerateDataKeyAsync(adminCmk.Arn, NoContext, cancellationToken)
				.ConfigureAwait(false);

			var generation = new DataKeyGeneration
			{
				KeyId = Guid.NewGuid(),
				CiphertextBlob = ciphertextBlob,
				CmkId = adminCmk.CmkId,
				CreatedAt = timeProvider.GetUtcNow(),
			};
			db.DataKeyGenerations.Add(generation);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { keyId = generation.KeyId, cmkArn = adminCmk.Arn }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.DataKeyRotated, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			return DataKeyRotationOutcome.Rotated;
		}).ConfigureAwait(false);
	}
}
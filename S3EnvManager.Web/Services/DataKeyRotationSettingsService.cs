using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class DataKeyRotationSettingsService(ApplicationDbContext db, IAuditLogger auditLogger)
	: IDataKeyRotationSettingsService
{
	public async Task<Int32> GetIntervalDaysAsync(CancellationToken cancellationToken = default)
	{
		var settings = await db.DataKeyRotationSettings.AsNoTracking()
			.SingleOrDefaultAsync(s => s.Id == Database.Models.DataKeyRotationSettings.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		return settings?.RotationIntervalDays ?? IDataKeyRotationSettingsService.DefaultDays;
	}

	public async Task SetIntervalDaysAsync(
		Int32 days, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		if (days < IDataKeyRotationSettingsService.MinDays || days > IDataKeyRotationSettingsService.MaxDays)
		{
			throw new ArgumentOutOfRangeException(nameof(days),
				$"로테이션 주기는 {IDataKeyRotationSettingsService.MinDays}~" +
				$"{IDataKeyRotationSettingsService.MaxDays}일 범위여야 합니다.");
		}

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var settings = await db.DataKeyRotationSettings
				.FromSqlInterpolated($"""
					SELECT * FROM "DataKeyRotationSettings"
					WHERE "Id" = {Database.Models.DataKeyRotationSettings.SingletonId} FOR UPDATE
					""")
				.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

			var previousDays = settings?.RotationIntervalDays;
			if (settings is null)
			{
				settings = new Database.Models.DataKeyRotationSettings
				{
					RotationIntervalDays = days,
					UpdatedAt = DateTimeOffset.UtcNow,
				};
				db.DataKeyRotationSettings.Add(settings);
			}
			else
			{
				settings.RotationIntervalDays = days;
				settings.UpdatedAt = DateTimeOffset.UtcNow;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { previousDays, newDays = days }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.DataKeyRotationIntervalChanged, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}
}
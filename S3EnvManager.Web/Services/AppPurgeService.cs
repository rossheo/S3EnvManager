using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public static class AppPurgeService
{
	public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(60);

	public static async Task PurgeEligibleAppsAsync(
		ApplicationDbContext db, ISecretObjectStore store, IPrimaryStorageSettingsStore primaryStorageSettingsStore,
		TimeProvider timeProvider, CancellationToken cancellationToken = default)
	{
		var cutoff = timeProvider.GetUtcNow() - RetentionPeriod;

		var eligibleAppIds = await db.Apps.AsNoTracking()
			.Where(a => a.DeletedAt != null && a.DeletedAt <= cutoff && a.PurgedAt == null)
			.Select(a => a.Id)
			.ToListAsync(cancellationToken).ConfigureAwait(false);
		if (eligibleAppIds.Count == 0)
		{
			return;
		}

		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false)
			?? throw new InvalidOperationException("주 저장소가 아직 프로비저닝되지 않았습니다.");

		foreach (var appId in eligibleAppIds)
		{
			await PurgeOneAsync(db, store, bucket, appId, timeProvider, cancellationToken).ConfigureAwait(false);
		}
	}

	// S3 DeleteObject는 멱등이라 잠금 없이도 데이터는 안전하지만, 불필요한 중복 호출과 PurgedAt
	// 갱신 경쟁을 막기 위해 App 하나씩 행 잠금으로 감싼다.
	private static async Task PurgeOneAsync(
		ApplicationDbContext db, ISecretObjectStore store, string bucket, Guid appId, TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var app = await db.Apps
				.FromSqlInterpolated($"SELECT * FROM \"Apps\" WHERE \"Id\" = {appId} FOR UPDATE")
				.Include(a => a.Envs)
				.SingleAsync(cancellationToken).ConfigureAwait(false);

			var cutoff = timeProvider.GetUtcNow() - RetentionPeriod;
			if (app.DeletedAt is null || app.DeletedAt > cutoff || app.PurgedAt is not null)
			{
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
				return;
			}

			try
			{
				foreach (var env in app.Envs)
				{
					var baseKey = $"{app.Name}/{env.Name.ToObjectSegment()}.env";
					await store.DeleteAsync(bucket, baseKey, cancellationToken).ConfigureAwait(false);

					var overwriteKey = $"{app.Name}/{env.Name.ToObjectSegment()}.overwrite.env";
					await store.DeleteAsync(bucket, overwriteKey, cancellationToken).ConfigureAwait(false);
				}

				app.PurgedAt = timeProvider.GetUtcNow();
				await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
				await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Exception) when (!cancellationToken.IsCancellationRequested)
			{
				// 이 App은 다음 주기에 재시도한다 - 나머지 App 처리를 막지 않는다.
				await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
			}
		}).ConfigureAwait(false);
	}
}
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class FeatureSwitchService(ApplicationDbContext db, IAuditLogger auditLogger)
	: IFeatureSwitchService
{
	public async Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default)
	{
		var defaultEnabled = FeatureSwitchKeys.Known
			.Where(k => k.Key == key)
			.Select(k => (bool?)k.DefaultEnabled)
			.FirstOrDefault();
		if (defaultEnabled is null)
		{
			return false;
		}

		var stored = await db.FeatureSwitches.AsNoTracking()
			.Where(f => f.Key == key)
			.Select(f => (bool?)f.Enabled)
			.SingleOrDefaultAsync(cancellationToken)
			.ConfigureAwait(false);
		return stored ?? defaultEnabled.Value;
	}

	public async Task<List<FeatureSwitchInfo>> ListAsync(CancellationToken cancellationToken = default)
	{
		var stored = await db.FeatureSwitches.AsNoTracking()
			.ToDictionaryAsync(f => f.Key, f => f.Enabled, cancellationToken)
			.ConfigureAwait(false);

		return FeatureSwitchKeys.Known
			.Select(k => new FeatureSwitchInfo(
				k.Key, stored.TryGetValue(k.Key, out var enabled) ? enabled : k.DefaultEnabled, k.Description))
			.ToList();
	}

	public async Task SetEnabledAsync(
		string key, bool enabled, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		if (!FeatureSwitchKeys.Known.Any(k => k.Key == key))
		{
			throw new ArgumentOutOfRangeException(nameof(key), $"알 수 없는 기능 스위치입니다: {key}");
		}

		// DataKeyRotationSettingsService와 동일한 이유로, Npgsql 재시도 실행 전략과 호환되도록
		// 트랜잭션 시작~커밋 전체를 delegate 안에 둔다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var existing = await db.FeatureSwitches
				.FromSqlInterpolated($"SELECT * FROM \"FeatureSwitches\" WHERE \"Key\" = {key} FOR UPDATE")
				.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

			var previousEnabled = existing?.Enabled;
			if (existing is null)
			{
				existing = new FeatureSwitch { Key = key, Enabled = enabled, UpdatedAt = DateTimeOffset.UtcNow };
				db.FeatureSwitches.Add(existing);
			}
			else
			{
				existing.Enabled = enabled;
				existing.UpdatedAt = DateTimeOffset.UtcNow;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			var details = System.Text.Json.JsonSerializer.Serialize(
				new { key, previousEnabled, newEnabled = enabled }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.FeatureSwitchChanged, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}
}
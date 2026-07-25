using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class UserNotificationAlertSwitchService(ApplicationDbContext db)
	: IUserNotificationAlertSwitchService
{
	public async Task<bool> IsEnabledAsync(
		string userId, string alertType, CancellationToken cancellationToken = default)
	{
		var defaultEnabled = UserNotificationAlertTypes.Known
			.Where(k => k.Key == alertType)
			.Select(k => (bool?)k.DefaultEnabled)
			.FirstOrDefault();
		if (defaultEnabled is null)
		{
			return false;
		}

		var stored = await db.UserNotificationAlertSwitches.AsNoTracking()
			.Where(s => s.UserId == userId && s.AlertType == alertType)
			.Select(s => (bool?)s.Enabled)
			.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		return stored ?? defaultEnabled.Value;
	}

	public async Task<List<UserNotificationAlertSwitchInfo>> ListAsync(
		string userId, CancellationToken cancellationToken = default)
	{
		var stored = await db.UserNotificationAlertSwitches.AsNoTracking()
			.Where(s => s.UserId == userId)
			.ToDictionaryAsync(s => s.AlertType, s => s.Enabled, cancellationToken).ConfigureAwait(false);

		return UserNotificationAlertTypes.Known
			.Select(k => new UserNotificationAlertSwitchInfo(
				k.Key,
				stored.TryGetValue(k.Key, out var enabled) ? enabled : k.DefaultEnabled,
				k.Description))
			.ToList();
	}

	public async Task SetEnabledAsync(
		string userId, string alertType, bool enabled, CancellationToken cancellationToken = default)
	{
		if (!UserNotificationAlertTypes.Known.Any(k => k.Key == alertType))
		{
			throw new ArgumentOutOfRangeException(nameof(alertType), $"알 수 없는 알림 종류입니다: {alertType}");
		}

		// DataKeyRotationSettingsService와 동일한 이유로, Npgsql 재시도 실행 전략과 호환되도록
		// 트랜잭션 시작~커밋 전체를 delegate 안에 둔다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var existing = await db.UserNotificationAlertSwitches
				.FromSqlInterpolated($"""
					SELECT * FROM "UserNotificationAlertSwitches"
					WHERE "UserId" = {userId} AND "AlertType" = {alertType} FOR UPDATE
					""")
				.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

			if (existing is null)
			{
				existing = new UserNotificationAlertSwitch
				{
					UserId = userId,
					AlertType = alertType,
					Enabled = enabled,
					UpdatedAt = DateTimeOffset.UtcNow,
				};
				db.UserNotificationAlertSwitches.Add(existing);
			}
			else
			{
				existing.Enabled = enabled;
				existing.UpdatedAt = DateTimeOffset.UtcNow;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
	}
}

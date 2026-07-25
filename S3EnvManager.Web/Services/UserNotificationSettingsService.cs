using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class UserNotificationSettingsService(
	ApplicationDbContext db, IDataProtectionProvider dataProtectionProvider)
	: IUserNotificationSettingsService
{
	public async Task<UserNotificationSettingsInfo> GetAsync(
		string userId, CancellationToken cancellationToken = default)
	{
		var stored = await db.UserNotificationSettings.AsNoTracking()
			.SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken).ConfigureAwait(false);
		if (stored is null)
		{
			return new UserNotificationSettingsInfo(null, null);
		}

		var webhookUrl = stored.ProtectedDiscordWebhookUrl is null
			? null
			: Protector().Unprotect(stored.ProtectedDiscordWebhookUrl);
		return new UserNotificationSettingsInfo(webhookUrl, stored.NotifyDaysBeforeExpiration);
	}

	public async Task<string?> SaveAsync(
		string userId, string? webhookUrl, Int32? dDayDays, CancellationToken cancellationToken = default)
	{
		string? protectedUrl = null;
		if (!string.IsNullOrWhiteSpace(webhookUrl))
		{
			var urlError = DiscordWebhookUrlValidator.Validate(webhookUrl);
			if (urlError is not null)
			{
				return urlError;
			}
			protectedUrl = Protector().Protect(webhookUrl);
		}

		if (dDayDays is not null &&
			(dDayDays < IUserNotificationSettingsService.MinNotifyDays
				|| dDayDays > IUserNotificationSettingsService.MaxNotifyDays))
		{
			return $"D-Day는 {IUserNotificationSettingsService.MinNotifyDays}~" +
				$"{IUserNotificationSettingsService.MaxNotifyDays} 범위여야 합니다.";
		}

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);

			var existing = await db.UserNotificationSettings
				.FromSqlInterpolated($"""
					SELECT * FROM "UserNotificationSettings" WHERE "UserId" = {userId} FOR UPDATE
					""")
				.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

			if (existing is null)
			{
				existing = new UserNotificationSettings
				{
					UserId = userId,
					ProtectedDiscordWebhookUrl = protectedUrl,
					NotifyDaysBeforeExpiration = dDayDays,
					UpdatedAt = DateTimeOffset.UtcNow,
				};
				db.UserNotificationSettings.Add(existing);
			}
			else
			{
				existing.ProtectedDiscordWebhookUrl = protectedUrl;
				existing.NotifyDaysBeforeExpiration = dDayDays;
				existing.UpdatedAt = DateTimeOffset.UtcNow;
			}
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);

		return null;
	}

	private IDataProtector Protector() =>
		dataProtectionProvider.CreateProtector(IUserNotificationSettingsService.ProtectorPurpose);
}

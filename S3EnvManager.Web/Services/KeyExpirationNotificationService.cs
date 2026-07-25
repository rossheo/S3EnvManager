using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

/// <summary>D-Day를 설정한 사용자마다 만료가 임박한(또는 이미 지난) 키를 모아 Discord로 알린다.
/// 재전송 정책은 매일 리마인드 - 중복 전송 방지 추적은 없다(사용자 명시적 요청).</summary>
public static class KeyExpirationNotificationService
{
	public static async Task CheckAndNotifyAsync(
		ApplicationDbContext db,
		IDiscordNotifier notifier,
		IDataProtectionProvider dataProtectionProvider,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		var now = timeProvider.GetUtcNow();
		var protector = dataProtectionProvider.CreateProtector(
			IUserNotificationSettingsService.ProtectorPurpose);

		var candidates = await db.UserNotificationSettings.AsNoTracking()
			.Where(s => s.NotifyDaysBeforeExpiration != null && s.ProtectedDiscordWebhookUrl != null)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		foreach (var settings in candidates)
		{
			var enabled = await IsKeyExpirationAlertEnabledAsync(db, settings.UserId, cancellationToken)
				.ConfigureAwait(false);
			if (!enabled)
			{
				continue;
			}

			var cutoff = now.AddDays(settings.NotifyDaysBeforeExpiration!.Value);
			var expiring = await db.KeyExpirations.AsNoTracking()
				.Where(k => k.ExpiresAt <= cutoff)
				.Join(db.Envs.AsNoTracking(), k => k.EnvId, e => e.Id, (k, e) => new { k, e })
				.Join(db.Apps.AsNoTracking(), ke => ke.e.AppId, a => a.Id, (ke, a) => new
				{
					ke.k.KeyName,
					ke.k.ExpiresAt,
					ke.k.IsOverwriteBundle,
					AppName = a.Name,
					EnvName = ke.e.Name,
				})
				.OrderBy(x => x.ExpiresAt)
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			// 공유 시크릿은 참조하는 Env마다 개별 KeyExpiration을 두지 않으므로(중복 알림 방지),
			// 여기서 SharedSecret.ExpiresAt을 직접 스캔해 같은 메시지에 병합한다.
			var expiringSharedSecrets = await db.SharedSecrets.AsNoTracking()
				.Where(s => s.ExpiresAt != null && s.ExpiresAt <= cutoff)
				.OrderBy(s => s.ExpiresAt)
				.ToListAsync(cancellationToken).ConfigureAwait(false);

			if (expiring.Count == 0 && expiringSharedSecrets.Count == 0)
			{
				continue;
			}

			var lines = expiring.Select(x =>
			{
				var dDayText = FormatDDay(x.ExpiresAt, now);
				var bundle = x.IsOverwriteBundle ? "(overwrite)" : "";
				return $"- {x.AppName}/{x.EnvName.ToObjectSegment()}{bundle} {x.KeyName}: " +
					$"{x.ExpiresAt:yyyy-MM-dd} ({dDayText})";
			});
			var sharedLines = expiringSharedSecrets.Select(s =>
			{
				var dDayText = FormatDDay(s.ExpiresAt!.Value, now);
				return $"- [공유] {s.Name}: {s.ExpiresAt:yyyy-MM-dd} ({dDayText})";
			});
			var content = $"**키 만료 알림** (D-Day {settings.NotifyDaysBeforeExpiration}일 설정)\n" +
				string.Join("\n", lines.Concat(sharedLines));

			var webhookUrl = protector.Unprotect(settings.ProtectedDiscordWebhookUrl!);
			await notifier.SendAsync(webhookUrl, content, cancellationToken).ConfigureAwait(false);
		}
	}

	private static string FormatDDay(DateTimeOffset expiresAt, DateTimeOffset now)
	{
		var daysLeft = (Int32)Math.Floor((expiresAt - now).TotalDays);
		return daysLeft < 0 ? $"D+{-daysLeft}(만료됨)" : $"D-{daysLeft}";
	}

	private static async Task<bool> IsKeyExpirationAlertEnabledAsync(
		ApplicationDbContext db, string userId, CancellationToken cancellationToken)
	{
		var stored = await db.UserNotificationAlertSwitches.AsNoTracking()
			.Where(s => s.UserId == userId && s.AlertType == UserNotificationAlertTypes.KeyExpiration)
			.Select(s => (bool?)s.Enabled)
			.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		return stored ?? true; // UserNotificationAlertTypes.Known의 KeyExpiration 기본값(true)과 일치.
	}
}

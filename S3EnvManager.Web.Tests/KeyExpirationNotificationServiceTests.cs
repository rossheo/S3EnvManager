using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>D-Day 경계값과 알림 스위치 off 케이스를 실 Postgres로 검증한다. Discord 발송 자체는
/// FakeDiscordNotifier로 가로채 실제 웹훅을 부르지 않는다.</summary>
public class KeyExpirationNotificationServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	private static readonly IDataProtectionProvider DataProtection = new EphemeralDataProtectionProvider();

	[Fact]
	public async Task Notifies_ForKeyInsideDDayWindow_ButNotForKeyOutsideWindow()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		var timeProvider = new FakeTimeProvider(now);
		await using var db = CreateDbContext();
		await ResetNotificationTablesAsync(db);
		var (app, env) = await RegisterAppAsync(db);
		var user = await RegisterUserAsync(db);
		var webhookUrl = UniqueWebhookUrl();
		await SetUserNotificationSettingsAsync(db, user.Id, webhookUrl, dDayDays: 7);

		AddExpiration(db, env.Id, "NEAR", now.AddDays(5));
		AddExpiration(db, env.Id, "FAR", now.AddDays(30));
		await db.SaveChangesAsync();

		var notifier = new FakeDiscordNotifier();
		await KeyExpirationNotificationService.CheckAndNotifyAsync(
			db, notifier, DataProtection, timeProvider, CancellationToken.None);

		// 다른 테스트가 남긴 사용자에게도 별도로 알림이 갈 수 있으므로(같은 Postgres를 공유),
		// 이 테스트의 웹훅 URL로 온 발송만 골라 확인한다.
		var sent = Assert.Single(notifier.Sent, s => s.WebhookUrl == webhookUrl);
		Assert.Contains("NEAR", sent.Content);
		Assert.DoesNotContain("FAR", sent.Content);
	}

	[Fact]
	public async Task Notifies_ForKeyExactlyAtDDayBoundary_AndForAlreadyExpiredKey()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		var timeProvider = new FakeTimeProvider(now);
		await using var db = CreateDbContext();
		await ResetNotificationTablesAsync(db);
		var (app, env) = await RegisterAppAsync(db);
		var user = await RegisterUserAsync(db);
		var webhookUrl = UniqueWebhookUrl();
		await SetUserNotificationSettingsAsync(db, user.Id, webhookUrl, dDayDays: 7);

		AddExpiration(db, env.Id, "EXACT_BOUNDARY", now.AddDays(7));
		AddExpiration(db, env.Id, "ALREADY_EXPIRED", now.AddDays(-1));
		await db.SaveChangesAsync();

		var notifier = new FakeDiscordNotifier();
		await KeyExpirationNotificationService.CheckAndNotifyAsync(
			db, notifier, DataProtection, timeProvider, CancellationToken.None);

		var sent = Assert.Single(notifier.Sent, s => s.WebhookUrl == webhookUrl);
		Assert.Contains("EXACT_BOUNDARY", sent.Content);
		Assert.Contains("ALREADY_EXPIRED", sent.Content);
	}

	[Fact]
	public async Task Skips_WhenKeyExpirationAlertSwitchIsDisabled()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		var timeProvider = new FakeTimeProvider(now);
		await using var db = CreateDbContext();
		await ResetNotificationTablesAsync(db);
		var (app, env) = await RegisterAppAsync(db);
		var user = await RegisterUserAsync(db);
		var webhookUrl = UniqueWebhookUrl();
		await SetUserNotificationSettingsAsync(db, user.Id, webhookUrl, dDayDays: 7);
		db.UserNotificationAlertSwitches.Add(new UserNotificationAlertSwitch
		{
			UserId = user.Id,
			AlertType = UserNotificationAlertTypes.KeyExpiration,
			Enabled = false,
			UpdatedAt = now,
		});

		AddExpiration(db, env.Id, "NEAR", now.AddDays(1));
		await db.SaveChangesAsync();

		var notifier = new FakeDiscordNotifier();
		await KeyExpirationNotificationService.CheckAndNotifyAsync(
			db, notifier, DataProtection, timeProvider, CancellationToken.None);

		Assert.DoesNotContain(notifier.Sent, s => s.WebhookUrl == webhookUrl);
	}

	[Fact]
	public async Task Skips_WhenNoExpirationFallsInsideWindow()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var now = DateTimeOffset.UtcNow;
		var timeProvider = new FakeTimeProvider(now);
		await using var db = CreateDbContext();
		await ResetNotificationTablesAsync(db);
		var (app, env) = await RegisterAppAsync(db);
		var user = await RegisterUserAsync(db);
		var webhookUrl = UniqueWebhookUrl();
		await SetUserNotificationSettingsAsync(db, user.Id, webhookUrl, dDayDays: 7);

		AddExpiration(db, env.Id, "FAR", now.AddDays(30));
		await db.SaveChangesAsync();

		var notifier = new FakeDiscordNotifier();
		await KeyExpirationNotificationService.CheckAndNotifyAsync(
			db, notifier, DataProtection, timeProvider, CancellationToken.None);

		Assert.DoesNotContain(notifier.Sent, s => s.WebhookUrl == webhookUrl);
	}

	// CheckAndNotifyAsync는 시스템 전체 App/Env의 만료 키를 대상으로 하는 설계라(사용자별
	// 소유 개념이 없음), 다른 테스트가 남긴 KeyExpirations/UserNotificationSettings 행이 이
	// 테스트의 판단에 섞여 들어갈 수 있다 - 각 테스트 시작 시 비운다.
	private static async Task ResetNotificationTablesAsync(ApplicationDbContext db)
	{
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"KeyExpirations\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"UserNotificationAlertSwitches\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"UserNotificationSettings\"");
	}

	private static string UniqueWebhookUrl() =>
		$"https://discord.com/api/webhooks/{Guid.NewGuid():N}/token-{Guid.NewGuid():N}";

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);

	private static async Task<(App App, Env Env)> RegisterAppAsync(ApplicationDbContext db)
	{
		var app = new App
		{
			Id = Guid.NewGuid(),
			Name = "notif-" + Guid.NewGuid().ToString("N")[..8],
			CreatedAt = DateTimeOffset.UtcNow,
		};
		var env = new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = EnvName.Dev };
		app.Envs.Add(env);
		db.Apps.Add(app);
		await db.SaveChangesAsync();
		return (app, env);
	}

	private static async Task<ApplicationUser> RegisterUserAsync(ApplicationDbContext db)
	{
		var email = "notif-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
		var user = new ApplicationUser { UserName = email, Email = email };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}

	private static async Task SetUserNotificationSettingsAsync(
		ApplicationDbContext db, string userId, string webhookUrl, Int32 dDayDays)
	{
		var protector = DataProtection.CreateProtector(IUserNotificationSettingsService.ProtectorPurpose);
		db.UserNotificationSettings.Add(new UserNotificationSettings
		{
			UserId = userId,
			ProtectedDiscordWebhookUrl = protector.Protect(webhookUrl),
			NotifyDaysBeforeExpiration = dDayDays,
			UpdatedAt = DateTimeOffset.UtcNow,
		});
		await db.SaveChangesAsync();
	}

	private static void AddExpiration(
		ApplicationDbContext db, Guid envId, string keyName, DateTimeOffset expiresAt)
	{
		db.KeyExpirations.Add(new KeyExpiration
		{
			Id = Guid.NewGuid(),
			EnvId = envId,
			IsOverwriteBundle = false,
			KeyName = keyName,
			ExpiresAt = expiresAt,
			UpdatedAt = DateTimeOffset.UtcNow,
		});
	}

	private sealed class FakeDiscordNotifier : IDiscordNotifier
	{
		public List<(string WebhookUrl, string Content)> Sent { get; } = [];

		public Task SendAsync(
			string webhookUrl, string content, CancellationToken cancellationToken = default)
		{
			Sent.Add((webhookUrl, content));
			return Task.CompletedTask;
		}
	}

	private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => now;
	}
}

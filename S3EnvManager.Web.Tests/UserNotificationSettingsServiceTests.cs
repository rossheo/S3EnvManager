using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>Notifications.razor가 실제로 호출하는 서비스 계층(UserNotificationSettingsService/
/// UserNotificationAlertSwitchService)을 실 Postgres + 실 DataProtection Protect/Unprotect로
/// 왕복 검증한다 - 헤드리스 브라우저를 쓸 수 없는 환경이라, 페이지 대신 페이지가 부르는 정확히
/// 같은 코드 경로를 검증한다.</summary>
public class UserNotificationSettingsServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	private static readonly IDataProtectionProvider DataProtection = new EphemeralDataProtectionProvider();

	[Fact]
	public async Task SaveThenGet_RoundTripsWebhookUrlAndDDay()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var user = await RegisterUserAsync(db);
		var settingsService = new UserNotificationSettingsService(db, DataProtection);

		var error = await settingsService.SaveAsync(
			user.Id, "https://discord.com/api/webhooks/12345/abcXYZ", 7);
		Assert.Null(error);

		await using var verifyDb = CreateDbContext();
		var verifyService = new UserNotificationSettingsService(verifyDb, DataProtection);
		var loaded = await verifyService.GetAsync(user.Id);
		Assert.Equal("https://discord.com/api/webhooks/12345/abcXYZ", loaded.WebhookUrl);
		Assert.Equal(7, loaded.NotifyDaysBeforeExpiration);

		// 저장된 원본이 평문이 아니라 DataProtection으로 감싸져 있는지 직접 확인한다.
		var raw = await verifyDb.UserNotificationSettings.FindAsync(user.Id);
		Assert.NotNull(raw);
		Assert.DoesNotContain("api/webhooks/12345/abcXYZ", raw!.ProtectedDiscordWebhookUrl);
	}

	[Fact]
	public async Task Save_RejectsMalformedWebhookUrl()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var user = await RegisterUserAsync(db);
		var settingsService = new UserNotificationSettingsService(db, DataProtection);

		var error = await settingsService.SaveAsync(user.Id, "https://evil.example.com/not-discord", 7);
		Assert.NotNull(error);

		var loaded = await settingsService.GetAsync(user.Id);
		Assert.Null(loaded.WebhookUrl);
	}

	[Fact]
	public async Task Save_RejectsOutOfRangeDDay()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var user = await RegisterUserAsync(db);
		var settingsService = new UserNotificationSettingsService(db, DataProtection);

		var error = await settingsService.SaveAsync(
			user.Id, "https://discord.com/api/webhooks/1/token", 0);
		Assert.NotNull(error);
	}

	[Fact]
	public async Task AlertSwitch_DefaultsEnabled_AndPersistsAfterToggleOff()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await using var db = CreateDbContext();
		var user = await RegisterUserAsync(db);
		var switchService = new UserNotificationAlertSwitchService(db);

		var defaultEnabled = await switchService.IsEnabledAsync(
			user.Id, UserNotificationAlertTypes.KeyExpiration);
		Assert.True(defaultEnabled);

		await switchService.SetEnabledAsync(user.Id, UserNotificationAlertTypes.KeyExpiration, false);

		await using var verifyDb = CreateDbContext();
		var verifySwitchService = new UserNotificationAlertSwitchService(verifyDb);
		var list = await verifySwitchService.ListAsync(user.Id);
		var keyExpirationSwitch = Assert.Single(
			list, s => s.AlertType == UserNotificationAlertTypes.KeyExpiration);
		Assert.False(keyExpirationSwitch.Enabled);
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);

	private static async Task<ApplicationUser> RegisterUserAsync(ApplicationDbContext db)
	{
		var email = "notifsettings-" + Guid.NewGuid().ToString("N")[..8] + "@example.com";
		var user = new ApplicationUser { UserName = email, Email = email };
		db.Users.Add(user);
		await db.SaveChangesAsync();
		return user;
	}
}

using Microsoft.EntityFrameworkCore;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>Feature 스위치(AllowRegistration)를 실제 Postgres로 검증한다 - DB에 행이 없을 때는
/// 코드에 정의된 기본값을 쓰고, 관리자가 바꾸면 그 값이 영속화되는지 확인한다.</summary>
public class FeatureSwitchServiceInfraTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task IsEnabledAsync_ReturnsCodeDefault_WhenNoRowExists()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = BuildDbContext();
		await db.FeatureSwitches
			.Where(f => f.Key == FeatureSwitchKeys.AllowRegistration)
			.ExecuteDeleteAsync();

		var service = new FeatureSwitchService(db, new NoOpAuditLogger());
		var enabled = await service.IsEnabledAsync(FeatureSwitchKeys.AllowRegistration);

		var expectedDefault = FeatureSwitchKeys.Known
			.Single(k => k.Key == FeatureSwitchKeys.AllowRegistration).DefaultEnabled;
		Assert.Equal(expectedDefault, enabled);
	}

	[Fact]
	public async Task ListAsync_IncludesAllKnownSwitches()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = BuildDbContext();
		var service = new FeatureSwitchService(db, new NoOpAuditLogger());

		var listed = await service.ListAsync();
		var listedKeys = listed.Select(s => s.Key).ToList();

		Assert.Contains(FeatureSwitchKeys.AllowRegistration, listedKeys);
		Assert.Contains(FeatureSwitchKeys.AllowForgotPassword, listedKeys);
		Assert.Contains(FeatureSwitchKeys.AllowResendEmailConfirmation, listedKeys);
		Assert.Equal(FeatureSwitchKeys.Known.Count, listed.Count);
	}

	[Fact]
	public async Task SetEnabledAsync_PersistsOverride_AndListAsyncReflectsIt()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = BuildDbContext();
		var service = new FeatureSwitchService(db, new NoOpAuditLogger());

		try
		{
			await service.SetEnabledAsync(FeatureSwitchKeys.AllowRegistration, false);
			Assert.False(await service.IsEnabledAsync(FeatureSwitchKeys.AllowRegistration));

			var listed = await service.ListAsync();
			var allowRegistration = listed.Single(s => s.Key == FeatureSwitchKeys.AllowRegistration);
			Assert.False(allowRegistration.Enabled);

			await service.SetEnabledAsync(FeatureSwitchKeys.AllowRegistration, true);
			Assert.True(await service.IsEnabledAsync(FeatureSwitchKeys.AllowRegistration));
		}
		finally
		{
			await service.SetEnabledAsync(FeatureSwitchKeys.AllowRegistration, true);
		}
	}

	[Fact]
	public async Task SetEnabledAsync_ThrowsForUnknownKey()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		await using var db = BuildDbContext();
		var service = new FeatureSwitchService(db, new NoOpAuditLogger());

		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
			() => service.SetEnabledAsync("NotARealSwitch", true));
	}

	private sealed class NoOpAuditLogger : IAuditLogger
	{
		public Task LogAsync(string eventType, string? actorUserId, Guid? appId, string? details,
			CancellationToken cancellationToken = default) =>
			Task.CompletedTask;
	}

	private static ApplicationDbContext BuildDbContext()
	{
		var options = new DbContextOptionsBuilder<ApplicationDbContext>()
			.UseNpgsql(PostgresConnectionString)
			.Options;
		return new ApplicationDbContext(options);
	}

	private static async Task<bool> IsPostgresAvailableAsync()
	{
		try
		{
			await using var connection = new NpgsqlConnection(PostgresConnectionString);
			await connection.OpenAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}
}
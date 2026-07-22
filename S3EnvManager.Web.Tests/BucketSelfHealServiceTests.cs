using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>실 S3는 쓰지 않는다 - fake로도 orphan 버킷 걱정 없이 검증 가능하다.</summary>
public class BucketSelfHealServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task HealAsync_EnablesAllAutoFixChecks_AndIsIdempotent_AndLogsAuditEventOnlyOnFirstRun()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var bucket = "fake-bucket-" + Guid.NewGuid().ToString("N")[..8];
		var s3 = new FakeBucketComplianceOperations();
		var service = new BucketSelfHealService(s3, new AuditLogger(CreateDbContext()));

		var first = await service.HealAsync(bucket);
		Assert.True(first.VersioningEnabled);
		Assert.True(first.PublicAccessBlocked);
		Assert.True(first.ObjectOwnershipEnforced);
		Assert.True(first.LifecycleRuleConfigured);
		Assert.False(first.TlsEnforced);

		var second = await service.HealAsync(bucket);
		Assert.Equal(first with { CheckedAt = default }, second with { CheckedAt = default });
		Assert.Equal(1, s3.EnableVersioningCallCount);
		Assert.Equal(1, s3.PutPublicAccessBlockCallCount);
		Assert.Equal(1, s3.EnforceObjectOwnershipCallCount);
		Assert.Equal(1, s3.AddLifecycleRuleCallCount);

		await using var verifyDb = CreateDbContext();
		var logs = await verifyDb.AuditLogs
			.Where(a => a.EventType == AuditEventTypes.BucketSelfHealed && a.Details!.Contains(bucket))
			.ToListAsync();
		Assert.Single(logs);
	}

	[Fact]
	public async Task HealAsync_DoesNotOverwriteExistingLifecycleRule()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var bucket = "fake-bucket-existing-" + Guid.NewGuid().ToString("N")[..8];
		var s3 = new FakeBucketComplianceOperations();
		s3.SeedExistingLifecycleRule(bucket);

		var service = new BucketSelfHealService(s3, new AuditLogger(CreateDbContext()));
		var report = await service.HealAsync(bucket);

		Assert.True(report.LifecycleRuleConfigured);
		Assert.Equal(0, s3.AddLifecycleRuleCallCount);
	}

	[Fact]
	public async Task HealAsync_DetectsTlsEnforcement_WithoutModifyingPolicy()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		var bucket = "fake-bucket-tls-" + Guid.NewGuid().ToString("N")[..8];
		var s3 = new FakeBucketComplianceOperations();
		var policy = "{\"Version\":\"2012-10-17\",\"Statement\":[{\"Sid\":\"DenyInsecure\",\"Effect\":\"Deny\"," +
			"\"Principal\":\"*\",\"Action\":\"s3:*\",\"Resource\":[\"arn:aws:s3:::" + bucket + "/*\"]," +
			"\"Condition\":{\"Bool\":{\"aws:SecureTransport\":\"false\"}}}]}";
		s3.SeedBucketPolicy(bucket, policy);

		var service = new BucketSelfHealService(s3, new AuditLogger(CreateDbContext()));
		var report = await service.HealAsync(bucket);

		Assert.True(report.TlsEnforced);
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

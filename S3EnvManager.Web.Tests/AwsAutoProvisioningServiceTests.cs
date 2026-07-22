using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>StorageEndpointClientFactory의 실버킷 생성 경로는 주입 지점이 없어
/// includeBucketProvisioning은 항상 false로 건너뛴다.</summary>
public class AwsAutoProvisioningServiceTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task EnsureProvisionedAsync_ProvisionsEverythingFromAdminCredentialAlone_AndSecondRunIsIdempotent()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedProvisioningStateAsync();

		var adminCredentialOverride = new RuntimeAwsCredentialsOverride();
		var appCredentialOverride = new RuntimeAwsCredentialsOverride();
		var primaryStorageOverride = new RuntimePrimaryStorageOverride();

		var sts = new FakeSecurityTokenService();
		var kmsAdmin = new FakeKmsKeyAdministration();
		var appIdentity = new FakeBootstrapAppIdentityProvisioner();
		var credentialStore = new AwsBootstrapCredentialStore(
			CreateDbContext(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
		var primaryStorageSettingsStore = new PrimaryStorageSettingsStore(CreateDbContext());
		var auditLogger = new AuditLogger(CreateDbContext());
		var registryService = new CmkRegistryService(
			CreateDbContext(), auditLogger, new FakeAppCredentialProvisioner(), new FakeSecretObjectStore(),
			new FakeKmsKeyOperations(), appIdentity, primaryStorageSettingsStore, kmsAdmin);
		var bucketSelfHeal = new BucketSelfHealService(new FakeBucketComplianceOperations(), auditLogger);

		var service = new AwsAutoProvisioningService(
			sts, kmsAdmin, appIdentity, registryService, credentialStore,
			appCredentialOverride, adminCredentialOverride, primaryStorageSettingsStore, primaryStorageOverride,
			bucketSelfHeal, new BucketHealthStatusStore(), auditLogger);

		var request = new ProvisioningRequest("irrelevant-bucket", "ap-northeast-2", CreateBucketIfMissing: true);
		var firstReport = await service.EnsureProvisionedAsync(request, includeBucketProvisioning: false);

		var firstReportDump = string.Join("\n",
			firstReport.Steps.Select(s => $"{s.Name}: {s.Status} - {s.Detail}"));
		Assert.Contains(firstReport.Steps, s => s.Name.Contains("STS") && s.Status == ProvisioningStepStatus.Done);
		Assert.Contains(firstReport.Steps,
			s => s.Name.Contains("primary") && s.Status == ProvisioningStepStatus.Done);
		Assert.Contains(firstReport.Steps,
			s => s.Name.Contains("app-facing") && s.Status == ProvisioningStepStatus.Done);
		Assert.Contains(firstReport.Steps, s => s.Name.Contains("키 정책") && s.Status == ProvisioningStepStatus.Done);
		Assert.Contains(firstReport.Steps,
			s => s.Name.Contains("CMK 레지스트리") && s.Status == ProvisioningStepStatus.Done);
		Assert.Contains(firstReport.Steps,
			s => s.Name.Contains("Access Key") && s.Status == ProvisioningStepStatus.Done);
		Assert.True(firstReport.Succeeded, firstReportDump);

		var registrations = await registryService.ListAsync();
		var adminRegistration = Assert.Single(registrations, r => r.Role == CmkRole.Admin);
		var appRegistration = Assert.Single(registrations, r => r.Role == CmkRole.App);
		Assert.Equal(CmkStatus.Active, adminRegistration.Status);
		Assert.Equal(CmkStatus.Active, appRegistration.Status);

		var issuedAppCredential = await credentialStore.GetAsync(CmkRole.App);
		Assert.NotNull(issuedAppCredential);
		Assert.True(appCredentialOverride.IsSet);

		var primaryPolicy = kmsAdmin.Keys[adminRegistration.Arn].Policy;
		Assert.Contains("EnableIamUserPermissions", primaryPolicy);
		Assert.Contains("AllowAdminEnvelopeAccess", primaryPolicy);

		var secondReport = await service.EnsureProvisionedAsync(request, includeBucketProvisioning: false);
		var secondReportDump = string.Join("\n",
			secondReport.Steps.Select(s => $"{s.Name}: {s.Status} - {s.Detail}"));
		Assert.Contains(secondReport.Steps, s => s.Name.Contains("STS") && s.Status == ProvisioningStepStatus.Done);
		Assert.True(secondReport.Succeeded, secondReportDump);

		var registrationsAfterRerun = await registryService.ListAsync();
		Assert.Equal(2, registrationsAfterRerun.Count);
		Assert.Contains(registrationsAfterRerun, r => r.Arn == adminRegistration.Arn);
		Assert.Contains(registrationsAfterRerun, r => r.Arn == appRegistration.Arn);

		var cmkStep = secondReport.Steps.Single(s => s.Name.Contains("primary"));
		Assert.Equal(ProvisioningStepStatus.AlreadyProvisioned, cmkStep.Status);
		var keyStep = secondReport.Steps.Single(s => s.Name.Contains("Access Key"));
		Assert.Equal(ProvisioningStepStatus.AlreadyProvisioned, keyStep.Status);
	}

	/// <summary>alias 없이 레지스트리에만 등록된 기존 CMK를 승계해야 한다 - alias 조회를
	/// 먼저 하면 이를 못 찾고 중복 CMK를 만들게 된다.</summary>
	[Fact]
	public async Task EnsureProvisionedAsync_AdoptsPreRegisteredCmkInsteadOfCreatingDuplicate()
	{
		if (!await IsEnvironmentAvailableAsync())
		{
			return;
		}

		await ResetSharedProvisioningStateAsync();

		var adminCredentialOverride = new RuntimeAwsCredentialsOverride();
		var appCredentialOverride = new RuntimeAwsCredentialsOverride();
		var primaryStorageOverride = new RuntimePrimaryStorageOverride();

		var sts = new FakeSecurityTokenService();
		var kmsAdmin = new FakeKmsKeyAdministration();
		var appIdentity = new FakeBootstrapAppIdentityProvisioner();
		var credentialStore = new AwsBootstrapCredentialStore(
			CreateDbContext(), new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
		var primaryStorageSettingsStore = new PrimaryStorageSettingsStore(CreateDbContext());
		var auditLogger = new AuditLogger(CreateDbContext());
		var registryService = new CmkRegistryService(
			CreateDbContext(), auditLogger, new FakeAppCredentialProvisioner(), new FakeSecretObjectStore(),
			new FakeKmsKeyOperations(), appIdentity, primaryStorageSettingsStore, kmsAdmin);
		var bucketSelfHeal = new BucketSelfHealService(new FakeBucketComplianceOperations(), auditLogger);

		var managedTag = new Dictionary<string, string> { ["s3envmanager-managed"] = "true" };
		var preExistingAdminArn = await kmsAdmin.CreateKeyAsync(
			"pre-existing manually configured admin CMK", managedTag);
		var preExistingAdminRegistration = await registryService.RegisterAsync(CmkRole.Admin, preExistingAdminArn);
		Assert.Equal(CmkStatus.Active, preExistingAdminRegistration.Status);

		var preExistingAppArn = await kmsAdmin.CreateKeyAsync(
			"pre-existing manually configured app-facing CMK", managedTag);
		var preExistingAppRegistration = await registryService.RegisterAsync(CmkRole.App, preExistingAppArn);
		Assert.Equal(CmkStatus.Active, preExistingAppRegistration.Status);

		var service = new AwsAutoProvisioningService(
			sts, kmsAdmin, appIdentity, registryService, credentialStore,
			appCredentialOverride, adminCredentialOverride, primaryStorageSettingsStore, primaryStorageOverride,
			bucketSelfHeal, new BucketHealthStatusStore(), auditLogger);

		var request = new ProvisioningRequest(Bucket: "", Region: "", CreateBucketIfMissing: false);
		var report = await service.EnsureProvisionedAsync(request, includeBucketProvisioning: false);

		var primaryStep = report.Steps.Single(s => s.Name.Contains("primary"));
		Assert.Equal(ProvisioningStepStatus.AlreadyProvisioned, primaryStep.Status);
		Assert.Contains("승계", primaryStep.Detail);
		Assert.Contains(preExistingAdminArn, primaryStep.Detail);

		var appFacingStep = report.Steps.Single(s => s.Name.Contains("app-facing"));
		Assert.Equal(ProvisioningStepStatus.AlreadyProvisioned, appFacingStep.Status);
		Assert.Contains("승계", appFacingStep.Detail);
		Assert.Contains(preExistingAppArn, appFacingStep.Detail);

		var registrations = await registryService.ListAsync();
		var adminRegistrations = registrations.Where(r => r.Role == CmkRole.Admin).ToList();
		Assert.Single(adminRegistrations);
		Assert.Equal(preExistingAdminArn, adminRegistrations[0].Arn);
		Assert.Equal(CmkStatus.Active, adminRegistrations[0].Status);

		var appRegistrations = registrations.Where(r => r.Role == CmkRole.App).ToList();
		Assert.Single(appRegistrations);
		Assert.Equal(preExistingAppArn, appRegistrations[0].Arn);
		Assert.Equal(CmkStatus.Active, appRegistrations[0].Status);

		var accessKeyStep = report.Steps.Single(s => s.Name.Contains("Access Key"));
		Assert.True(accessKeyStep.Status != ProvisioningStepStatus.Failed, accessKeyStep.Detail);
	}

	private static async Task ResetSharedProvisioningStateAsync()
	{
		await using var db = CreateDbContext();
		// FK Restrict 제약 순서상 이 순서로 지워야 한다.
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"AppCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DbBackupAccountCredentials\"");
		await db.Database.ExecuteSqlRawAsync("DELETE FROM \"DataKeyGenerations\"");
		await db.CmkRegistrations.ExecuteDeleteAsync();
		await db.AwsBootstrapCredentials.Where(c => c.Role == CmkRole.App).ExecuteDeleteAsync();
	}

	private static Task<bool> IsEnvironmentAvailableAsync() => TestEnvironment.IsPostgresAvailableAsync();

	private static ApplicationDbContext CreateDbContext() =>
		new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
}

using Amazon.IdentityManagement;
using Amazon.KeyManagementService;
using Amazon.SecurityToken;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using MudBlazor.Services;
using S3EnvManager.Database;
using S3EnvManager.Sops;
using S3EnvManager.Web.Components;
using S3EnvManager.Web.Components.Account;
using S3EnvManager.Web.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddMudServices();

builder.Services.AddRazorComponents()
	.AddInteractiveServerComponents();

builder.Services.AddMemoryCache();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
	{
		options.DefaultScheme = IdentityConstants.ApplicationScheme;
		options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
	})
	.AddIdentityCookies();

builder.AddNpgsqlDbContext<ApplicationDbContext>("s3envmanagerdb");
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

// DataProtection 키링을 Postgres에 영속화한다 - 안 하면 컨테이너 재시작마다 키가 사라져
// 로그인 세션이 무효화된다.
builder.Services.AddDataProtection()
	.PersistKeysToDbContext<ApplicationDbContext>();

// 위 영속화만으로는 키가 평문 저장된다 - 인증서(XmlEncryptor/Decryptor)로 감싸고, 인증서
// 자체는 PFX로 DB에 저장하되 비밀번호만 DB 밖에서 관리한다. 비밀번호 미설정 시 보호 없이 동작.
var dataProtectionCertificateCache = new DataProtectionCertificateCache();
builder.Services.AddSingleton(dataProtectionCertificateCache);
builder.Services.Configure<DataProtectionCertificateOptions>(
	builder.Configuration.GetSection("DataProtectionCertificate"));
var dataProtectionCertificatePassword = builder.Configuration["DataProtectionCertificate:Password"];
if (!string.IsNullOrEmpty(dataProtectionCertificatePassword))
{
	builder.Services.AddOptions<KeyManagementOptions>()
		.PostConfigure(options =>
			options.XmlEncryptor = new CachedCertificateXmlEncryptor(dataProtectionCertificateCache));
	builder.Services.AddHostedService<DataProtectionCertificateRotationBackgroundService>();
}

builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
	// IdentityNoOpEmailSender는 실제로 메일을 보내지 않는다 - true로 두면 회원가입 직후
	// 다시 로그인할 방법이 없어진다.
	options.SignIn.RequireConfirmedAccount = false;
	options.Stores.SchemaVersion = ApplicationIdentitySchema.Version;

	options.Password.RequiredLength = 10;
	options.Password.RequireDigit = false;
	options.Password.RequireLowercase = false;
	options.Password.RequireUppercase = false;
	options.Password.RequireNonAlphanumeric = false;

	options.Lockout.AllowedForNewUsers = true;
	options.Lockout.MaxFailedAccessAttempts = 5;
	options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
})
	.AddRoles<IdentityRole>()
	.AddEntityFrameworkStores<ApplicationDbContext>()
	.AddSignInManager()
	.AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// 부트스트랩 자격증명은 admin(unkeyed)/app(keyed) 두 role로 나뉜다 - app 자격증명이 유출돼도
// app CMK Encrypt만 가능해 아무것도 복호화할 수 없다. 둘 다 AwsBootstrapCredentialStore로
// DB에 암호화 저장되고 기동 시 오버라이드에 적용된다.
var runtimeAwsCredentialsOverride = new RuntimeAwsCredentialsOverride();
builder.Services.AddSingleton<IRuntimeAwsCredentialsOverride>(runtimeAwsCredentialsOverride);
builder.Services.AddKeyedSingleton<IRuntimeAwsCredentialsOverride>(
	CmkRole.App, new RuntimeAwsCredentialsOverride());
var awsOptions = builder.Configuration.GetAWSOptions();
awsOptions.Credentials = new OverridableAwsCredentials(runtimeAwsCredentialsOverride);
builder.Services.AddDefaultAWSOptions(awsOptions);
builder.Services.AddAWSService<IAmazonKeyManagementService>();
builder.Services.AddAWSService<IAmazonIdentityManagementService>();
builder.Services.AddAWSService<IAmazonSecurityTokenService>();
builder.Services.AddKeyedSingleton<IAmazonKeyManagementService>(CmkRole.App, (sp, _) =>
{
	var appOverride = sp.GetRequiredKeyedService<IRuntimeAwsCredentialsOverride>(CmkRole.App);
	var adminOverride = sp.GetRequiredService<IRuntimeAwsCredentialsOverride>();
	var appAwsOptions = builder.Configuration.GetAWSOptions();
	// app 자격증명이 없으면 admin으로 대신 시도한다(admin 정책에 app CMK Encrypt가 포함돼 있음).
	appAwsOptions.Credentials = new OverridableAwsCredentials(appOverride, adminOverride);
	return appAwsOptions.CreateServiceClient<IAmazonKeyManagementService>();
});
builder.Services.AddKeyedSingleton<IKmsKeyOperations>(CmkRole.App, (sp, _) =>
	new AwsKmsKeyOperations(sp.GetRequiredKeyedService<IAmazonKeyManagementService>(CmkRole.App)));
builder.Services.AddScoped<IAwsBootstrapCredentialStore, AwsBootstrapCredentialStore>();

builder.Services.AddScoped<IKmsKeyAdministration, AwsKmsKeyAdministration>();
builder.Services.AddScoped<IBootstrapAppIdentityProvisioner, AwsBootstrapAppIdentityProvisioner>();
builder.Services.AddScoped<IAwsAutoProvisioningService, AwsAutoProvisioningService>();

// 주 저장소(항상 AWS S3)는 화면에서 명시적으로 저장해야 동작한다 - 배포 설정 기본 엔드포인트로
// 암묵적으로 폴백하지 않는다.
builder.Services.AddSingleton<IRuntimePrimaryStorageOverride, RuntimePrimaryStorageOverride>();
builder.Services.AddScoped<IPrimaryStorageSettingsStore, PrimaryStorageSettingsStore>();
builder.Services.AddSingleton<IAmazonS3ClientProvider, AmazonS3ClientProvider>();

builder.Services.AddScoped<IBucketComplianceOperations, S3BucketComplianceOperations>();
builder.Services.AddScoped<IBucketSelfHealService, BucketSelfHealService>();
builder.Services.AddScoped<IAppRegistrationService, AppRegistrationService>();
builder.Services.AddScoped<IKmsKeyOperations, AwsKmsKeyOperations>();
builder.Services.AddScoped<ISecretObjectStore, S3SecretObjectStore>();
builder.Services.AddScoped<ISecretBundleService, SecretBundleService>();
builder.Services.AddSingleton<IDataKeyCache, DataKeyCache>();
builder.Services.AddScoped<IAppSecretKeyCipher, AppSecretKeyCipher>();
builder.Services.AddScoped<IAppCredentialProvisioner, IamAppCredentialProvisioner>();
builder.Services.AddScoped<IAppCredentialService, AppCredentialService>();
builder.Services.Configure<CmkBootstrapOptions>(builder.Configuration.GetSection("Cmk"));
builder.Services.Configure<InitialAdminSetupOptions>(builder.Configuration.GetSection("InitialAdminSetup"));
builder.Services.AddScoped<IUserRoleService, UserRoleService>();
builder.Services.AddSingleton<IBucketHealthStatusStore, BucketHealthStatusStore>();
builder.Services.AddHostedService<BucketSelfHealBackgroundService>();
builder.Services.AddScoped<IAppDeletionService, AppDeletionService>();
builder.Services.AddHostedService<AppPurgeBackgroundService>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddHostedService<AuditLogRetentionBackgroundService>();
builder.Services.AddScoped<ICmkRegistryService, CmkRegistryService>();
builder.Services.AddScoped<IDataKeyRotationSettingsService, DataKeyRotationSettingsService>();
builder.Services.AddScoped<IFeatureSwitchService, FeatureSwitchService>();
builder.Services.AddHostedService<DataKeyRotationBackgroundService>();
builder.Services.AddScoped<IDbBackupAccountService, DbBackupAccountService>();

var app = builder.Build();

// Identity 역할 시드/초기 관리자 승격은 S3EnvManager.MigrationService가 먼저 실행해 끝내둔다.
using (var scope = app.Services.CreateScope())
{
	var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

	// DataProtection 인증서 캐시를 다른 Protect/Unprotect 호출보다 먼저 채운다 - 아래
	// AwsBootstrapCredentialStore.GetAsync()가 기존 암호화된 자격증명을 복호화하려면 필요하다.
	if (!string.IsNullOrEmpty(dataProtectionCertificatePassword))
	{
		var dataProtectionCertificateOptions = scope.ServiceProvider
			.GetRequiredService<Microsoft.Extensions.Options.IOptions<DataProtectionCertificateOptions>>().Value;
		var loadedCertificates = await DataProtectionCertificateStore.LoadAllAsync(
			db, dataProtectionCertificatePassword);
		if (loadedCertificates.Count == 0)
		{
			var issuedCertificate = await DataProtectionCertificateStore.IssueAndSaveAsync(
				db, dataProtectionCertificatePassword, dataProtectionCertificateOptions.ValidityYears,
				TimeProvider.System);
			dataProtectionCertificateCache.ReplaceSnapshot([issuedCertificate]);
		}
		else
		{
			dataProtectionCertificateCache.ReplaceSnapshot(loadedCertificates);
		}
	}

	// DB에 저장된 admin/app 부트스트랩 자격증명을 다른 AWS/KMS 호출보다 먼저 오버라이드에 적용한다.
	var credentialStore = scope.ServiceProvider.GetRequiredService<IAwsBootstrapCredentialStore>();
	var adminCredentialOverride = scope.ServiceProvider.GetRequiredService<IRuntimeAwsCredentialsOverride>();
	var appCredentialOverride = scope.ServiceProvider
		.GetRequiredKeyedService<IRuntimeAwsCredentialsOverride>(CmkRole.App);
	var storedAdminCredential = await credentialStore.GetAsync(CmkRole.Admin);
	if (storedAdminCredential is { } admin)
	{
		adminCredentialOverride.Set(admin.AccessKeyId, admin.SecretAccessKey);
	}
	var storedAppCredential = await credentialStore.GetAsync(CmkRole.App);
	if (storedAppCredential is { } appCred)
	{
		appCredentialOverride.Set(appCred.AccessKeyId, appCred.SecretAccessKey);
	}

	// 주 저장소 엔드포인트도 같은 시점에 적용한다.
	var primaryStorageSettingsStore = scope.ServiceProvider.GetRequiredService<IPrimaryStorageSettingsStore>();
	var primaryStorageOverride = scope.ServiceProvider.GetRequiredService<IRuntimePrimaryStorageOverride>();
	var storedPrimaryStorage = await primaryStorageSettingsStore.GetAsync();
	if (storedPrimaryStorage is not null)
	{
		primaryStorageOverride.Set(storedPrimaryStorage);
	}

	var cmkOptions = scope.ServiceProvider
		.GetRequiredService<Microsoft.Extensions.Options.IOptions<CmkBootstrapOptions>>();
	await CmkBootstrapService.EnsureBootstrapCmksSeededAsync(db, cmkOptions.Value);

	var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
	var initialAdminSetupOptions = scope.ServiceProvider
		.GetRequiredService<Microsoft.Extensions.Options.IOptions<InitialAdminSetupOptions>>();
	await InitialAdminSetupTokenService.LogIfBootstrapPendingAsync(
		db, userManager, initialAdminSetupOptions.Value, app.Logger);

	// AutoProvisioningSelfHeal이 켜져 있고 admin 자격증명이 있으면 부트스트랩 리소스를 재확인한다.
	// 기본은 꺼져 있고, 실패해도 기동을 막지 않는다.
	var featureSwitchService = scope.ServiceProvider.GetRequiredService<IFeatureSwitchService>();
	if (adminCredentialOverride.IsSet &&
		await featureSwitchService.IsEnabledAsync(
			S3EnvManager.Database.Models.FeatureSwitchKeys.AutoProvisioningSelfHeal))
	{
		try
		{
			var autoProvisioningService = scope.ServiceProvider.GetRequiredService<IAwsAutoProvisioningService>();
			await autoProvisioningService.EnsureProvisionedAsync(
				new ProvisioningRequest(Bucket: "", Region: "", CreateBucketIfMissing: false),
				actorUserId: null, includeBucketProvisioning: false);
		}
		catch (Exception ex)
		{
			app.Logger.LogWarning(ex,
				"AutoProvisioningSelfHeal 기동 시 자동 재확인에 실패했습니다 - 다음 기동이나 /settings/bootstrap에서 수동으로 재시도할 수 있습니다.");
		}
	}

	// pg_dump 전용 읽기 전용 계정은 최초 설치 시에만 생성한다 - 이미 있으면 회전시키지 않는다
	// (자동화가 같은 비밀번호를 계속 참조하므로).
	var dbBackupAccountService = scope.ServiceProvider.GetRequiredService<IDbBackupAccountService>();
	await dbBackupAccountService.EnsureAsync();
}

if (app.Environment.IsDevelopment())
{
	app.UseMigrationsEndPoint();
}
else
{
	app.UseExceptionHandler("/Error", createScopeForErrors: true);
	app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
	.AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();

app.MapDefaultEndpoints();

app.Run();

using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Database;

/// <summary>DataProtection 키링을 Postgres에 영속화해, 컨테이너 재시작/다중 인스턴스 간에도
/// 인증 쿠키/antiforgery 토큰이 유효하게 유지한다.</summary>
public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
	: IdentityDbContext<ApplicationUser>(options), IDataProtectionKeyContext
{
	public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

	public DbSet<App> Apps => Set<App>();

	public DbSet<Env> Envs => Set<Env>();

	public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

	public DbSet<CmkRegistration> CmkRegistrations => Set<CmkRegistration>();

	public DbSet<DataKeyGeneration> DataKeyGenerations => Set<DataKeyGeneration>();

	public DbSet<AppCredential> AppCredentials => Set<AppCredential>();

	public DbSet<DataKeyRotationSettings> DataKeyRotationSettings => Set<DataKeyRotationSettings>();

	public DbSet<DbBackupAccountCredential> DbBackupAccountCredentials => Set<DbBackupAccountCredential>();

	public DbSet<FeatureSwitch> FeatureSwitches => Set<FeatureSwitch>();

	public DbSet<AwsBootstrapCredential> AwsBootstrapCredentials => Set<AwsBootstrapCredential>();

	public DbSet<PrimaryStorageSettings> PrimaryStorageSettings => Set<PrimaryStorageSettings>();

	public DbSet<InitialAdminSetupToken> InitialAdminSetupTokens => Set<InitialAdminSetupToken>();

	public DbSet<DataProtectionCertificate> DataProtectionCertificates => Set<DataProtectionCertificate>();

	public DbSet<KeyExpiration> KeyExpirations => Set<KeyExpiration>();

	public DbSet<UserNotificationSettings> UserNotificationSettings => Set<UserNotificationSettings>();

	public DbSet<UserNotificationAlertSwitch> UserNotificationAlertSwitches =>
		Set<UserNotificationAlertSwitch>();

	public DbSet<SharedSecret> SharedSecrets => Set<SharedSecret>();

	public DbSet<SharedSecretAppGrant> SharedSecretAppGrants => Set<SharedSecretAppGrant>();

	public DbSet<SharedSecretReference> SharedSecretReferences => Set<SharedSecretReference>();

	protected override void OnModelCreating(ModelBuilder builder)
	{
		base.OnModelCreating(builder);

		builder.Entity<App>(entity =>
		{
			entity.HasIndex(a => a.Name).IsUnique();
			entity.HasMany(a => a.Envs)
				.WithOne(e => e.App)
				.HasForeignKey(e => e.AppId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		builder.Entity<Env>(entity =>
		{
			// App 하나당 같은 EnvName은 하나뿐이다(dev/staging/product 고정 3종).
			entity.HasIndex(e => new { e.AppId, e.Name }).IsUnique();
		});

		builder.Entity<CmkRegistration>(entity =>
		{
			entity.HasKey(c => c.CmkId);
			entity.HasIndex(c => c.Arn).IsUnique();
		});

		builder.Entity<DataKeyGeneration>(entity =>
		{
			entity.HasKey(d => d.KeyId);
			entity.HasOne(d => d.Cmk)
				.WithMany()
				.HasForeignKey(d => d.CmkId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<AuditLog>(entity =>
		{
			entity.HasIndex(a => a.OccurredAt);
		});

		builder.Entity<AppCredential>(entity =>
		{
			entity.HasIndex(c => c.AccessKeyId).IsUnique();
			entity.HasOne(c => c.App)
				.WithMany()
				.HasForeignKey(c => c.AppId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(c => c.DataKey)
				.WithMany()
				.HasForeignKey(c => c.DataKeyId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<DataKeyRotationSettings>(entity =>
		{
			entity.HasKey(s => s.Id);
		});

		builder.Entity<DbBackupAccountCredential>(entity =>
		{
			entity.HasKey(c => c.Id);
			entity.HasOne<DataKeyGeneration>()
				.WithMany()
				.HasForeignKey(c => c.DataKeyId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<FeatureSwitch>(entity =>
		{
			entity.HasKey(f => f.Key);
		});

		builder.Entity<AwsBootstrapCredential>(entity =>
		{
			entity.HasKey(c => c.Role);
		});

		builder.Entity<PrimaryStorageSettings>(entity =>
		{
			entity.HasKey(s => s.Id);
		});

		builder.Entity<InitialAdminSetupToken>(entity =>
		{
			entity.HasKey(t => t.Id);
		});

		builder.Entity<DataProtectionCertificate>(entity =>
		{
			entity.HasKey(c => c.Id);
			entity.HasIndex(c => c.NotBefore);
		});

		builder.Entity<KeyExpiration>(entity =>
		{
			entity.HasIndex(k => new { k.EnvId, k.IsOverwriteBundle, k.KeyName }).IsUnique();
			entity.HasOne(k => k.Env)
				.WithMany()
				.HasForeignKey(k => k.EnvId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		builder.Entity<UserNotificationSettings>(entity =>
		{
			entity.HasKey(s => s.UserId);
			entity.HasOne(s => s.User)
				.WithMany()
				.HasForeignKey(s => s.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		builder.Entity<UserNotificationAlertSwitch>(entity =>
		{
			entity.HasKey(s => new { s.UserId, s.AlertType });
			entity.HasOne(s => s.User)
				.WithMany()
				.HasForeignKey(s => s.UserId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		builder.Entity<SharedSecret>(entity =>
		{
			entity.HasIndex(s => s.Name).IsUnique();
			entity.HasOne(s => s.DataKey)
				.WithMany()
				.HasForeignKey(s => s.DataKeyId)
				.OnDelete(DeleteBehavior.Restrict);
		});

		builder.Entity<SharedSecretAppGrant>(entity =>
		{
			entity.HasIndex(g => new { g.SharedSecretId, g.AppId }).IsUnique();
			entity.HasOne(g => g.SharedSecret)
				.WithMany()
				.HasForeignKey(g => g.SharedSecretId)
				.OnDelete(DeleteBehavior.Cascade);
			entity.HasOne(g => g.App)
				.WithMany()
				.HasForeignKey(g => g.AppId)
				.OnDelete(DeleteBehavior.Cascade);
		});

		builder.Entity<SharedSecretReference>(entity =>
		{
			entity.HasIndex(r => new { r.EnvId, r.IsOverwriteBundle, r.KeyName }).IsUnique();
			// 참조가 남아있으면 SharedSecret을 DB 레벨에서 삭제할 수 없다 - dangling reference
			// 방지의 최종 안전장치(서비스 코드는 반드시 삭제 전에 모든 참조를 detach해야 한다).
			entity.HasOne(r => r.SharedSecret)
				.WithMany()
				.HasForeignKey(r => r.SharedSecretId)
				.OnDelete(DeleteBehavior.Restrict);
			entity.HasOne(r => r.Env)
				.WithMany()
				.HasForeignKey(r => r.EnvId)
				.OnDelete(DeleteBehavior.Cascade);
		});
	}
}
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
	}
}
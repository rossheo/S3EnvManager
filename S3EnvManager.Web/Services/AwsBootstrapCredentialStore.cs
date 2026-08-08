using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class AwsBootstrapCredentialStore(
	ApplicationDbContext db, IDataProtectionProvider dataProtectionProvider,
	ILogger<AwsBootstrapCredentialStore> logger)
	: IAwsBootstrapCredentialStore
{
	private readonly IDataProtector _protector =
		dataProtectionProvider.CreateProtector("S3EnvManager.AwsBootstrapCredential.v1");

	public async Task SaveAsync(
		CmkRole role, string accessKeyId, string secretAccessKey, CancellationToken cancellationToken = default)
	{
		var protectedAccessKeyId = _protector.Protect(accessKeyId);
		var protectedSecretAccessKey = _protector.Protect(secretAccessKey);

		var existing = await db.AwsBootstrapCredentials.SingleOrDefaultAsync(c => c.Role == role, cancellationToken)
			.ConfigureAwait(false);
		if (existing is null)
		{
			db.AwsBootstrapCredentials.Add(new AwsBootstrapCredential
			{
				Role = role,
				ProtectedAccessKeyId = protectedAccessKeyId,
				ProtectedSecretAccessKey = protectedSecretAccessKey,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}
		else
		{
			existing.ProtectedAccessKeyId = protectedAccessKeyId;
			existing.ProtectedSecretAccessKey = protectedSecretAccessKey;
			existing.UpdatedAt = DateTimeOffset.UtcNow;
		}
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<(string AccessKeyId, string SecretAccessKey)?> GetAsync(
		CmkRole role, CancellationToken cancellationToken = default)
	{
		var entry = await db.AwsBootstrapCredentials.AsNoTracking()
			.SingleOrDefaultAsync(c => c.Role == role, cancellationToken).ConfigureAwait(false);
		if (entry is null)
		{
			return null;
		}

		// DataProtection 키링이 사라졌거나(볼륨 유실, DB만 복원) 이 행을 감쌌던 키가 폐기되면
		// Unprotect가 실패한다. 그대로 던지면 이 메서드를 기동 경로에서 부르는 Program.cs가
		// 호스트째로 죽어 재설정할 화면조차 못 띄운다 - "자격증명 미설정"으로 내려앉혀
		// /settings/bootstrap에서 다시 등록할 수 있게 한다(SaveAsync가 이 행을 덮어쓴다).
		try
		{
			return (
				_protector.Unprotect(entry.ProtectedAccessKeyId),
				_protector.Unprotect(entry.ProtectedSecretAccessKey));
		}
		catch (CryptographicException ex)
		{
			logger.LogError(ex,
				"{Role} 부트스트랩 자격증명을 복호화하지 못했습니다(DataProtection 키링 유실/폐기 의심) - " +
				"미설정 상태로 계속 진행합니다. /settings/bootstrap에서 자격증명을 다시 등록하세요.", role);
			return null;
		}
	}

	public async Task ClearAsync(CmkRole role, CancellationToken cancellationToken = default)
	{
		var existing = await db.AwsBootstrapCredentials.SingleOrDefaultAsync(c => c.Role == role, cancellationToken)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			db.AwsBootstrapCredentials.Remove(existing);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
		}
	}
}
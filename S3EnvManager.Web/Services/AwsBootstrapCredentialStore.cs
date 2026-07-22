using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class AwsBootstrapCredentialStore(
	ApplicationDbContext db, IDataProtectionProvider dataProtectionProvider)
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

		return (
			_protector.Unprotect(entry.ProtectedAccessKeyId), _protector.Unprotect(entry.ProtectedSecretAccessKey));
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
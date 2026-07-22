using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class PrimaryStorageSettingsStore(ApplicationDbContext db) : IPrimaryStorageSettingsStore
{
	public async Task SaveAsync(
		string? region, string? bucket, CancellationToken cancellationToken = default)
	{
		var existing = await db.PrimaryStorageSettings.SingleOrDefaultAsync(
			s => s.Id == Database.Models.PrimaryStorageSettings.SingletonId, cancellationToken).ConfigureAwait(false);
		if (existing is null)
		{
			db.PrimaryStorageSettings.Add(new Database.Models.PrimaryStorageSettings
			{
				Region = region,
				Bucket = bucket,
				UpdatedAt = DateTimeOffset.UtcNow,
			});
		}
		else
		{
			existing.Region = region;
			existing.Bucket = bucket;
			existing.UpdatedAt = DateTimeOffset.UtcNow;
		}
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}

	public async Task<StorageEndpointSettings?> GetAsync(CancellationToken cancellationToken = default)
	{
		var entry = await db.PrimaryStorageSettings.AsNoTracking()
			.SingleOrDefaultAsync(s => s.Id == Database.Models.PrimaryStorageSettings.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		return entry is null ? null : new StorageEndpointSettings(entry.Region);
	}

	public async Task<string?> GetLastProvisionedBucketAsync(CancellationToken cancellationToken = default)
	{
		var entry = await db.PrimaryStorageSettings.AsNoTracking()
			.SingleOrDefaultAsync(s => s.Id == Database.Models.PrimaryStorageSettings.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		return entry?.Bucket;
	}
}

using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

/// <summary>기동 시 admin/app role의 Active CMK가 없을 때만 등록한다(멱등).</summary>
public static class CmkBootstrapService
{
	public static async Task EnsureBootstrapCmksSeededAsync(
		ApplicationDbContext db, CmkBootstrapOptions options, TimeProvider timeProvider,
		CancellationToken cancellationToken = default)
	{
		await EnsureRoleSeededAsync(db, CmkRole.Admin, options.AdminArn, timeProvider, cancellationToken)
			.ConfigureAwait(false);
		await EnsureRoleSeededAsync(db, CmkRole.App, options.AppArn, timeProvider, cancellationToken)
			.ConfigureAwait(false);
	}

	private static async Task EnsureRoleSeededAsync(
		ApplicationDbContext db, CmkRole role, string? arn, TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(arn))
		{
			return;
		}

		var hasActive = await db.CmkRegistrations
			.AnyAsync(c => c.Role == role && c.Status == CmkStatus.Active, cancellationToken)
			.ConfigureAwait(false);
		if (hasActive)
		{
			return;
		}

		db.CmkRegistrations.Add(new CmkRegistration
		{
			CmkId = Guid.NewGuid(),
			Arn = arn,
			Role = role,
			Status = CmkStatus.Active,
			CreatedAt = timeProvider.GetUtcNow(),
		});
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
	}
}
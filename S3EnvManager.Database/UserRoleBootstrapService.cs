using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace S3EnvManager.Database;

/// <summary>역할이 없는 사용자는 최소 권한 원칙에 따라 Guest로 기본 배정한다. "첫 관리자"
/// 부트스트랩은 <see cref="InitialAdminSetupTokenService"/>가 회원가입 시점에 처리한다.</summary>
public static class UserRoleBootstrapService
{
	public static async Task EnsureDefaultRolesAssignedAsync(
		ApplicationDbContext db, UserManager<ApplicationUser> userManager,
		CancellationToken cancellationToken = default)
	{
		// 전원에 대해 GetRolesAsync를 부르면 N+1 쿼리가 되므로, 역할 없는 사용자만 한 번에 골라낸다.
		var rolelessUsers = await db.Users
			.Where(u => !db.UserRoles.Any(ur => ur.UserId == u.Id))
			.ToListAsync(cancellationToken)
			.ConfigureAwait(false);
		foreach (var user in rolelessUsers)
		{
			await userManager.AddToRoleAsync(user, IdentityRoleNames.Guest).ConfigureAwait(false);
		}
	}
}
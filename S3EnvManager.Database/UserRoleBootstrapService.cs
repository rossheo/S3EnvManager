using Microsoft.AspNetCore.Identity;

namespace S3EnvManager.Database;

/// <summary>역할이 없는 사용자는 최소 권한 원칙에 따라 Guest로 기본 배정한다. "첫 관리자"
/// 부트스트랩은 <see cref="InitialAdminSetupTokenService"/>가 회원가입 시점에 처리한다.</summary>
public static class UserRoleBootstrapService
{
	public static async Task EnsureDefaultRolesAssignedAsync(
		UserManager<ApplicationUser> userManager, CancellationToken cancellationToken = default)
	{
		foreach (var user in userManager.Users.ToList())
		{
			var roles = await userManager.GetRolesAsync(user);
			if (roles.Count == 0)
			{
				await userManager.AddToRoleAsync(user, IdentityRoleNames.Guest);
			}
		}
	}
}
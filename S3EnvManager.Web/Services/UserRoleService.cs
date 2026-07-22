using Microsoft.AspNetCore.Identity;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class UserRoleService(UserManager<ApplicationUser> userManager) : IUserRoleService
{
	public async Task<List<UserWithRole>> ListAsync(CancellationToken cancellationToken = default)
	{
		var users = userManager.Users.ToList();
		var result = new List<UserWithRole>(users.Count);
		foreach (var user in users)
		{
			var roles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
			var isLockedOut = await userManager.IsLockedOutAsync(user).ConfigureAwait(false);
			result.Add(new UserWithRole(
				user.Id, user.Email ?? user.UserName ?? user.Id, roles.FirstOrDefault(), isLockedOut));
		}
		return result;
	}

	public async Task SetRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default)
	{
		var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false)
			?? throw new InvalidOperationException("사용자를 찾을 수 없습니다.");

		var currentRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
		var rolesToRemove = currentRoles.Where(r => r != roleName).ToList();
		if (rolesToRemove.Count > 0)
		{
			var removeResult = await userManager.RemoveFromRolesAsync(user, rolesToRemove).ConfigureAwait(false);
			if (!removeResult.Succeeded)
			{
				throw new InvalidOperationException(
					$"기존 역할을 해제하지 못했습니다: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
			}
		}
		if (!currentRoles.Contains(roleName))
		{
			var addResult = await userManager.AddToRoleAsync(user, roleName).ConfigureAwait(false);
			if (!addResult.Succeeded)
			{
				throw new InvalidOperationException(
					$"새 역할을 부여하지 못했습니다: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
			}
		}
	}

	public async Task SetLockedOutAsync(
		string userId, bool lockedOut, CancellationToken cancellationToken = default)
	{
		var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false)
			?? throw new InvalidOperationException("사용자를 찾을 수 없습니다.");

		var result = await userManager.SetLockoutEndDateAsync(user, lockedOut ? DateTimeOffset.MaxValue : null)
			.ConfigureAwait(false);
		if (!result.Succeeded)
		{
			throw new InvalidOperationException(
				$"잠금 상태를 변경하지 못했습니다: {string.Join(", ", result.Errors.Select(e => e.Description))}");
		}
	}
}
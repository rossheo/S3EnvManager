using Microsoft.AspNetCore.Identity;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

public sealed class UserRoleService(UserManager<ApplicationUser> userManager, IAuditLogger auditLogger)
	: IUserRoleService
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

	public async Task SetRoleAsync(
		string userId, string roleName, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false)
			?? throw new InvalidOperationException("사용자를 찾을 수 없습니다.");

		var currentRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
		// remove/add 이후에는 이전 역할을 알 수 없으므로 지금 잡아둔다.
		var previousRole = currentRoles.FirstOrDefault();
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

		// remove/add는 하나의 트랜잭션이 아니므로 "바꾸려던 의도"가 아니라 "실제로 도달한 상태"를
		// 남긴다 - 중간에 실패하면 여기까지 오지 않는다.
		if (previousRole != roleName)
		{
			var details = System.Text.Json.JsonSerializer.Serialize(
				new { targetUserId = userId, from = previousRole, to = roleName }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.UserRoleChanged, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	public async Task SetLockedOutAsync(
		string userId, bool lockedOut, string? actorUserId = null, CancellationToken cancellationToken = default)
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

		var lockoutDetails = System.Text.Json.JsonSerializer.Serialize(
			new { targetUserId = userId, lockedOut }, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.UserLockoutChanged, actorUserId, appId: null, lockoutDetails, cancellationToken)
			.ConfigureAwait(false);
	}
}
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
		string userId, string roleName, string? actorUserId = null,
		CancellationToken cancellationToken = default)
	{
		EnsureNotSelf(userId, actorUserId, "자기 자신의 역할은 변경할 수 없습니다.");

		var user = await userManager.FindByIdAsync(userId).ConfigureAwait(false)
			?? throw new InvalidOperationException("사용자를 찾을 수 없습니다.");

		var currentRoles = await userManager.GetRolesAsync(user).ConfigureAwait(false);
		// remove/add 이후에는 이전 역할을 알 수 없으므로 지금 잡아둔다. 하나만 갖는 것이 이 서비스의
		// 규약이지만 실제로 그렇다고 가정하지 않는다 - Account/Register.razor가 UserManager를 직접
		// 써서 역할을 부여하므로 배타성이 깨진 상태가 들어올 수 있고, 그때 FirstOrDefault()로 하나만
		// 집으면 실제 권한 변경이 감사 로그 없이 지나간다.
		var previousRoles = currentRoles.OrderBy(r => r, StringComparer.Ordinal).ToList();
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
		// 남긴다 - 중간에 실패하면 여기까지 오지 않는다. 실제로 무언가 바뀐 경우에만 기록한다
		// (역할을 하나라도 뗐거나 새로 붙였으면 변경이다).
		var roleAdded = !currentRoles.Contains(roleName);
		if (rolesToRemove.Count > 0 || roleAdded)
		{
			var details = System.Text.Json.JsonSerializer.Serialize(
				new { targetUserId = userId, from = previousRoles, to = roleName }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.UserRoleChanged, actorUserId, appId: null, details, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	public async Task SetLockedOutAsync(
		string userId, bool lockedOut, string? actorUserId = null,
		CancellationToken cancellationToken = default)
	{
		EnsureNotSelf(userId, actorUserId, "자기 자신의 계정 잠금 상태는 변경할 수 없습니다.");

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

	// 유일한 Administrator가 스스로를 강등/잠금하면 CMK 등록·부트스트랩 화면에 아무도 못 들어가고
	// 복구 경로가 DB 직접 수정뿐이다. Users.razor가 이미 해당 컨트롤을 Disabled로 막지만 그건
	// 화면 쪽 방어라, 다른 호출자가 생기면 그대로 뚫린다.
	//
	// actorUserId는 선택 파라미터라 넘기지 않는 호출자는 이 가드를 지나간다 - 여기서 막는 것은
	// "행위자를 아는 호출에서의 자기 변경"까지다. 첫 Administrator 승격은 UserManager를 직접
	// 쓰는 Account/Register.razor 경로라 이 가드에 걸리지 않는다.
	private static void EnsureNotSelf(string userId, string? actorUserId, string message)
	{
		if (actorUserId is not null && string.Equals(userId, actorUserId, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(message);
		}
	}
}
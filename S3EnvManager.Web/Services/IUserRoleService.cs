namespace S3EnvManager.Web.Services;

public sealed record UserWithRole(string UserId, string Email, string? Role, bool IsLockedOut);

public interface IUserRoleService
{
	Task<List<UserWithRole>> ListAsync(CancellationToken cancellationToken = default);

	// Administrator/Member/Guest는 서로 배타적이므로 정확히 하나만 갖도록 만든다.
	Task SetRoleAsync(string userId, string roleName, CancellationToken cancellationToken = default);

	Task SetLockedOutAsync(string userId, bool lockedOut, CancellationToken cancellationToken = default);
}
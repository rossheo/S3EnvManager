using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public interface IAppCredentialService
{
	// SecretAccessKey는 "발급 시 1회만 노출" 방식이 아니라 이후 RevealAsync로도 다시 볼 수 있다.
	Task<(AppCredential Credential, string SecretAccessKey)> IssueAsync(
		Guid appId, string? actorUserId = null, CancellationToken cancellationToken = default);

	Task<List<AppCredential>> ListAsync(Guid appId, CancellationToken cancellationToken = default);

	Task<string> RevealAsync(Guid credentialId, CancellationToken cancellationToken = default);

	Task RevokeAsync(
		Guid credentialId, string? actorUserId = null, CancellationToken cancellationToken = default);
}
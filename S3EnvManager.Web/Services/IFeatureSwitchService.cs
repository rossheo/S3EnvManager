namespace S3EnvManager.Web.Services;

public sealed record FeatureSwitchInfo(string Key, bool Enabled, string Description);

public interface IFeatureSwitchService
{
	Task<bool> IsEnabledAsync(string key, CancellationToken cancellationToken = default);

	Task<List<FeatureSwitchInfo>> ListAsync(CancellationToken cancellationToken = default);

	Task SetEnabledAsync(
		string key, bool enabled, string? actorUserId = null, CancellationToken cancellationToken = default);
}
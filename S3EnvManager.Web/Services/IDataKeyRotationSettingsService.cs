namespace S3EnvManager.Web.Services;

public interface IDataKeyRotationSettingsService
{
	public const Int32 MinDays = 1;
	public const Int32 MaxDays = 3650;
	public const Int32 DefaultDays = 7;

	Task<Int32> GetIntervalDaysAsync(CancellationToken cancellationToken = default);

	Task SetIntervalDaysAsync(
		Int32 days, string? actorUserId = null, CancellationToken cancellationToken = default);
}
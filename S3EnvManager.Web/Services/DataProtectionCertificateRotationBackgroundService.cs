using Microsoft.Extensions.Options;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

// 인증서는 몇 년 단위로 유효하므로 하루 간격 확인으로 충분하다.
public sealed class DataProtectionCertificateRotationBackgroundService(
	IServiceScopeFactory scopeFactory, DataProtectionCertificateCache cache,
	ILogger<DataProtectionCertificateRotationBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromDays(1);

	protected override Task ExecuteCycleAsync(
		IServiceProvider services, CancellationToken cancellationToken) =>
		DataProtectionCertificateRotationService.RotateIfDueAsync(
			services.GetRequiredService<ApplicationDbContext>(),
			services.GetRequiredService<IOptions<DataProtectionCertificateOptions>>().Value,
			cache,
			services.GetRequiredService<IAuditLogger>(),
			TimeProvider.System, cancellationToken);
}

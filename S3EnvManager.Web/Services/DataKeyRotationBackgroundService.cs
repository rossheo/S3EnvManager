using S3EnvManager.Database;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

/// <summary>설정된 주기(기본 14일)가 지나면 새 데이터 키 세대를 발급한다.
/// 최소 로테이션 주기가 1일이므로 1시간 간격으로 확인해도 충분하다.</summary>
public sealed class DataKeyRotationBackgroundService(
	IServiceScopeFactory scopeFactory, ILogger<DataKeyRotationBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromHours(1);

	protected override Task ExecuteCycleAsync(IServiceProvider services, CancellationToken cancellationToken) =>
		DataKeyRotationService.RotateIfDueAsync(
			services.GetRequiredService<ApplicationDbContext>(),
			services.GetRequiredService<IKmsKeyOperations>(),
			services.GetRequiredService<IAuditLogger>(),
			TimeProvider.System, cancellationToken);
}

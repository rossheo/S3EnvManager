using S3EnvManager.Database;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

/// <summary>설정된 주기(기본 14일)가 지나면 새 데이터 키 세대를 발급한다.
/// 최소 로테이션 주기가 1일이므로 1시간 간격으로 확인해도 충분하다.</summary>
public sealed class DataKeyRotationBackgroundService(IServiceScopeFactory scopeFactory) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using (var scope = scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
				var kms = scope.ServiceProvider.GetRequiredService<IKmsKeyOperations>();
				var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
				await DataKeyRotationService.RotateIfDueAsync(
					db, kms, auditLogger, TimeProvider.System, stoppingToken)
					.ConfigureAwait(false);
			}

			try
			{
				await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}
		}
	}
}
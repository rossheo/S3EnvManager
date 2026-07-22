using Microsoft.Extensions.Options;
using S3EnvManager.Database;

namespace S3EnvManager.Web.Services;

// 인증서는 몇 년 단위로 유효하므로 하루 간격 확인으로 충분하다.
public sealed class DataProtectionCertificateRotationBackgroundService(
	IServiceScopeFactory scopeFactory, DataProtectionCertificateCache cache) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			using (var scope = scopeFactory.CreateScope())
			{
				var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
				var options = scope.ServiceProvider
					.GetRequiredService<IOptions<DataProtectionCertificateOptions>>().Value;
				var auditLogger = scope.ServiceProvider.GetRequiredService<IAuditLogger>();
				await DataProtectionCertificateRotationService.RotateIfDueAsync(
					db, options, cache, auditLogger, TimeProvider.System, stoppingToken)
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
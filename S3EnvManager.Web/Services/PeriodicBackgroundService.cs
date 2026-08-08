namespace S3EnvManager.Web.Services;

/// <summary>일정 주기로 한 사이클을 도는 배경 서비스의 공통 뼈대.
///
/// .NET 6+의 기본값이 <c>BackgroundServiceExceptionBehavior.StopHost</c>라
/// <see cref="BackgroundService.ExecuteAsync"/>에서 예외가 새어나가면 **호스트 전체가 종료된다** -
/// 주기 작업 하나가 몇 시간 뒤 정상 동작 중인 앱을 죽인다(실제로 KeyExpirationNotification이
/// DataProtection 키링 문제로 그렇게 만들었다). 배경 서비스 6개 중 5개가 이 가드를 빠뜨렸기에
/// 각자 try/catch를 복제하는 대신 여기서 한 번만 처리한다 - 새 배경 서비스가 빠뜨릴 수 없다.
///
/// 실패한 사이클은 삼키고 다음 주기에 재시도한다. HostOptions로
/// BackgroundServiceExceptionBehavior.Ignore를 쓰는 방법도 있지만, 그건 그 서비스를 영구히
/// 멈추므로(재시작되지 않는다) 여기서 원하는 동작이 아니다.</summary>
public abstract class PeriodicBackgroundService(IServiceScopeFactory scopeFactory, ILogger logger)
	: BackgroundService
{
	protected abstract TimeSpan Interval { get; }

	/// <summary>한 주기에 할 일. 스코프는 호출자가 만들고 정리한다.</summary>
	protected abstract Task ExecuteCycleAsync(IServiceProvider services, CancellationToken cancellationToken);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				using var scope = scopeFactory.CreateScope();
				await ExecuteCycleAsync(scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
			}
			catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
			{
				logger.LogError(ex,
					"{Service} 주기 작업이 실패했습니다 - {Interval} 뒤 다시 시도합니다.", GetType().Name, Interval);
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

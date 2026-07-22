namespace S3EnvManager.Web.Services;

// 주기적으로 재확인해, 누군가 버킷 설정을 아웃오브밴드로 바꿔도 다시 복구한다.
public sealed class BucketSelfHealBackgroundService(IServiceScopeFactory scopeFactory) : BackgroundService
{
	private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			await HealPrimaryBucketAsync(stoppingToken).ConfigureAwait(false);

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

	private async Task HealPrimaryBucketAsync(CancellationToken cancellationToken)
	{
		using var scope = scopeFactory.CreateScope();
		var primaryStorageSettingsStore = scope.ServiceProvider.GetRequiredService<IPrimaryStorageSettingsStore>();
		var selfHeal = scope.ServiceProvider.GetRequiredService<IBucketSelfHealService>();
		var healthStatusStore = scope.ServiceProvider.GetRequiredService<IBucketHealthStatusStore>();

		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false);
		if (bucket is null)
		{
			return;
		}

		try
		{
			var report = await selfHeal.HealAsync(bucket, cancellationToken).ConfigureAwait(false);
			healthStatusStore.Set(report);
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			// 이번 주기의 자가 치유가 실패해도 다음 주기(10분 뒤)에 재시도된다.
		}
	}
}
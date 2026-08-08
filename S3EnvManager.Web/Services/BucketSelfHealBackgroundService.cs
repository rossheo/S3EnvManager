namespace S3EnvManager.Web.Services;

// 주기적으로 재확인해, 누군가 버킷 설정을 아웃오브밴드로 바꿔도 다시 복구한다.
public sealed class BucketSelfHealBackgroundService(
	IServiceScopeFactory scopeFactory, ILogger<BucketSelfHealBackgroundService> logger)
	: PeriodicBackgroundService(scopeFactory, logger)
{
	protected override TimeSpan Interval => TimeSpan.FromMinutes(10);

	// 예전에는 자체 try/catch로 실패를 조용히 삼켜 로그가 한 줄도 남지 않았다 - 이제 기반
	// 클래스가 잡아 기록하고 다음 주기에 재시도한다.
	protected override async Task ExecuteCycleAsync(
		IServiceProvider services, CancellationToken cancellationToken)
	{
		var primaryStorageSettingsStore = services.GetRequiredService<IPrimaryStorageSettingsStore>();
		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false);
		if (bucket is null)
		{
			return;
		}

		var selfHeal = services.GetRequiredService<IBucketSelfHealService>();
		var healthStatusStore = services.GetRequiredService<IBucketHealthStatusStore>();
		var report = await selfHeal.HealAsync(bucket, cancellationToken).ConfigureAwait(false);
		healthStatusStore.Set(report);
	}
}

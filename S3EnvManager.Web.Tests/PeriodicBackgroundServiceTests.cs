using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>.NET 6+의 기본값(BackgroundServiceExceptionBehavior.StopHost)에서는 ExecuteAsync를
/// 빠져나온 예외가 호스트 전체를 종료시킨다 - 주기 작업 하나가 실행 중인 앱을 죽인다.
/// 기반 클래스가 그 예외를 잡고 다음 주기에 재시도하는지 확인한다.</summary>
public class PeriodicBackgroundServiceTests
{
	private static IServiceScopeFactory ScopeFactory() =>
		new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

	[Fact]
	public async Task ThrowingCycle_DoesNotEscape_AndKeepsRetrying()
	{
		var logger = new RecordingLogger();
		using var service = new ThrowingService(ScopeFactory(), logger);

		await service.StartAsync(CancellationToken.None);

		// 두 번째 사이클까지 들어오면 "예외가 새지 않았고 루프가 계속된다"가 증명된다.
		Assert.True(
			await service.ReachedSecondCycle.Task.WaitAsync(TimeSpan.FromSeconds(10)),
			"두 번째 주기에 진입하지 못했습니다 - 첫 예외에서 루프가 멈췄습니다.");

		// ExecuteTask가 완료(=예외로 종료)되지 않고 계속 돌아야 한다.
		Assert.False(service.ExecuteTask!.IsCompleted);

		await service.StopAsync(CancellationToken.None);
		Assert.True(logger.ErrorCount >= 1, "실패를 조용히 삼키면 안 된다 - 에러 로그가 남아야 한다.");
	}

	// 종료 시 취소로 생기는 예외까지 "주기 작업 실패"로 기록하면, 정상 종료할 때마다 에러 로그가
	// 쌓여 진짜 실패와 구분이 안 된다.
	[Fact]
	public async Task Cancellation_StopsLoop_WithoutLoggingFailure()
	{
		var logger = new RecordingLogger();
		using var service = new BlockingService(ScopeFactory(), logger);

		await service.StartAsync(CancellationToken.None);
		Assert.True(await service.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));

		await service.StopAsync(CancellationToken.None);

		Assert.Equal(0, logger.ErrorCount);
	}

	private sealed class ThrowingService(IServiceScopeFactory scopeFactory, ILogger logger)
		: PeriodicBackgroundService(scopeFactory, logger)
	{
		private Int32 _cycleCount;

		public TaskCompletionSource<bool> ReachedSecondCycle { get; } = new();

		protected override TimeSpan Interval => TimeSpan.FromMilliseconds(20);

		protected override Task ExecuteCycleAsync(
			IServiceProvider services, CancellationToken cancellationToken)
		{
			if (Interlocked.Increment(ref _cycleCount) >= 2)
			{
				ReachedSecondCycle.TrySetResult(true);
			}
			throw new InvalidOperationException("이 주기는 항상 실패한다.");
		}
	}

	private sealed class BlockingService(IServiceScopeFactory scopeFactory, ILogger logger)
		: PeriodicBackgroundService(scopeFactory, logger)
	{
		public TaskCompletionSource<bool> Entered { get; } = new();

		protected override TimeSpan Interval => TimeSpan.FromMilliseconds(20);

		protected override async Task ExecuteCycleAsync(
			IServiceProvider services, CancellationToken cancellationToken)
		{
			Entered.TrySetResult(true);
			// 종료 신호가 올 때까지 붙잡고 있다가 OperationCanceledException으로 빠져나온다.
			await Task.Delay(Timeout.Infinite, cancellationToken);
		}
	}

	private sealed class RecordingLogger : ILogger
	{
		private Int32 _errorCount;

		public Int32 ErrorCount => Volatile.Read(ref _errorCount);

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			if (logLevel >= LogLevel.Error)
			{
				Interlocked.Increment(ref _errorCount);
			}
		}
	}
}

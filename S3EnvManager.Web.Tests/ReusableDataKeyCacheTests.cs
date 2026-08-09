using S3EnvManager.Sops;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>ReusableDataKeyCache의 한도(MaxAge/MaxReuses)만 순수 단위 테스트로 검증한다 - DB 없이
/// 시간을 직접 제어할 수 있어야 경계값을 확인할 수 있다. 통합 경로(App 저장이 실제로 이 한도를
/// 지키는지)는 ReusableDataKeyOnSaveTests가 다룬다.</summary>
public class ReusableDataKeyCacheTests
{
	private static SopsWrappedDataKey Wrapped() => new([1], [2], [3]);

	[Fact]
	public void TryGet_JustBeforeMaxAge_StillHits()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app", "admin-arn", "app-arn", Wrapped());

		time.Advance(ReusableDataKeyCache.MaxAge - TimeSpan.FromSeconds(1));

		Assert.True(cache.TryGet("app", "admin-arn", "app-arn", out _));
	}

	[Fact]
	public void TryGet_AtOrAfterMaxAge_Misses_AndEvictsEntry()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app", "admin-arn", "app-arn", Wrapped());

		time.Advance(ReusableDataKeyCache.MaxAge);

		Assert.False(cache.TryGet("app", "admin-arn", "app-arn", out _));

		// 만료로 한 번 제거됐으니, 시계를 되돌려도(테스트 목적) 다시 채워지지 않는다 - Set을
		// 다시 호출해야 한다.
		time.Advance(-ReusableDataKeyCache.MaxAge);
		Assert.False(cache.TryGet("app", "admin-arn", "app-arn", out _));
	}

	// 키를 만든 저장 1회가 앞에 있으므로, 한 데이터 키가 보호하는 번들 수는 MaxReuses + 1이다 -
	// 여기서는 그 뒤쪽 MaxReuses만 재현한다.
	[Fact]
	public void TryGet_UpToMaxReuses_Hits_ThenMissesAndEvicts()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app", "admin-arn", "app-arn", Wrapped());

		for (var i = 0; i < ReusableDataKeyCache.MaxReuses; i++)
		{
			Assert.True(
				cache.TryGet("app", "admin-arn", "app-arn", out _),
				$"{i + 1}번째 재사용까지는 한도({ReusableDataKeyCache.MaxReuses}) 안이라 성공해야 한다.");
		}

		// 한도를 넘긴 다음 조회는 실패하고, 항목 자체가 제거된다.
		Assert.False(cache.TryGet("app", "admin-arn", "app-arn", out _));
	}

	[Fact]
	public void TryGet_DifferentAppNameOrArn_NeverSharesEntry()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app-a", "admin-arn", "app-arn", Wrapped());

		Assert.False(cache.TryGet("app-b", "admin-arn", "app-arn", out _));
		Assert.False(cache.TryGet("app-a", "other-admin-arn", "app-arn", out _));
		Assert.False(cache.TryGet("app-a", "admin-arn", "other-app-arn", out _));
	}

	// 3134e9c가 스위퍼를 추가한 이유 - 조회가 없으면 만료된 항목(과 그 평문 데이터 키)이
	// "최대 10분" 약속을 어기고 프로세스가 끝날 때까지 메모리에 남았다. 스위퍼가 실제로
	// 조회 없이도 지우는지 확인해야 그 버그가 안 돌아온다.
	//
	// 스위퍼가 지웠다는 것을, TryGet 자신의 즉시 만료 체크와 구분해서 증명해야 한다 - 그래서
	// 스윕 후 시계를 MaxAge 미만으로 되돌린다. 항목이 여전히 있다면(스위퍼가 안 지웠다면)
	// TryGet은 "안 늙었으니" 히트해야 한다. 미스가 나온다는 것은 스위퍼가 실제로 지웠다는 뜻이다.
	[Fact]
	public void Sweep_RemovesExpiredEntry_WithoutAnyQuery()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app", "admin-arn", "app-arn", Wrapped());

		time.Advance(ReusableDataKeyCache.MaxAge + TimeSpan.FromSeconds(1));
		time.FireTimer();

		time.Advance(-(ReusableDataKeyCache.MaxAge + TimeSpan.FromSeconds(1)));
		Assert.False(cache.TryGet("app", "admin-arn", "app-arn", out _));
	}

	// 반대로, 아직 안 늙은 항목을 스위퍼가 건드리면 안 된다.
	[Fact]
	public void Sweep_LeavesFreshEntryAlone()
	{
		var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
		using var cache = new ReusableDataKeyCache(time);
		cache.Set("app", "admin-arn", "app-arn", Wrapped());

		time.Advance(ReusableDataKeyCache.MaxAge - TimeSpan.FromSeconds(1));
		time.FireTimer();

		Assert.True(cache.TryGet("app", "admin-arn", "app-arn", out _));
	}

	private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
	{
		private DateTimeOffset _now = now;
		private TimerCallback? _sweepCallback;
		private object? _sweepState;

		public void Advance(TimeSpan delta) => _now += delta;

		public override DateTimeOffset GetUtcNow() => _now;

		// ReusableDataKeyCache는 스위퍼 타이머 하나만 만든다 - 기본 CreateTimer는 실제 벽시계로
		// 도는 System.Threading.Timer를 만들어 단위 테스트에서는 결정적으로 발동시킬 수 없으므로,
		// 콜백만 붙잡아뒀다가 FireTimer()로 직접 호출한다.
		public override ITimer CreateTimer(
			TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			_sweepCallback = callback;
			_sweepState = state;
			return new NoOpTimer();
		}

		public void FireTimer() => _sweepCallback?.Invoke(_sweepState);

		private sealed class NoOpTimer : ITimer
		{
			public bool Change(TimeSpan dueTime, TimeSpan period) => true;

			public void Dispose()
			{
			}

			public ValueTask DisposeAsync() => ValueTask.CompletedTask;
		}
	}
}

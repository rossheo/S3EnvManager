using System.Collections.Concurrent;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

/// <summary>번들 저장 1회는 KMS를 2회(GenerateDataKey + Encrypt) 쓴다. 감싼 데이터 키 한 벌을
/// 잠시 재사용하면 그 창 안의 저장들이 KMS를 0회 쓴다.
///
/// **보안 트레이드오프가 있다.** 데이터 키 하나가 여러 번들을 보호하게 되므로, 그 키가 유출되면
/// 한 번들이 아니라 그 창 안에 저장된 전부가 노출된다. 그래서 기본은 꺼져 있고
/// (FeatureSwitchKeys.ReuseDataKeyOnSave), 켜도 AWS Encryption SDK 권장대로 수명과 사용 횟수에
/// 한도를 건다.
///
/// 캐시 범위는 (appName, adminCmkArn, appCmkArn)이다. appName은 KMS encryption context에
/// 들어가므로 App 경계를 넘어 재사용하면 트레일러가 거짓 context를 주장해 그 번들이 영구히
/// 복호화 불가능해진다. ARN을 포함하므로 CMK 승격이 일어나면 다음 저장은 자연히 캐시를 비껴간다.</summary>
public interface IReusableDataKeyCache
{
	bool TryGet(string appName, string adminCmkArn, string appCmkArn, out SopsWrappedDataKey wrapped);

	void Set(string appName, string adminCmkArn, string appCmkArn, SopsWrappedDataKey wrapped);
}

public sealed class ReusableDataKeyCache : IReusableDataKeyCache, IDisposable
{
	private readonly TimeProvider _timeProvider;
	private readonly KmsMetrics? _metrics;
	private readonly ITimer _sweeper;

	public ReusableDataKeyCache(TimeProvider timeProvider, KmsMetrics? metrics = null)
	{
		_timeProvider = timeProvider;
		_metrics = metrics;

		// 만료를 TryGet에서만 확인하면, 한 번 저장하고 더 이상 저장하지 않는 App의 평문 데이터
		// 키가 프로세스가 끝날 때까지 메모리에 남는다 - "최대 10분"이라는 약속이 지켜지지 않는다.
		// 조회가 없어도 실제로 지우려면 주기적으로 쓸어내야 한다.
		_sweeper = timeProvider.CreateTimer(_ => Sweep(), state: null, SweepInterval, SweepInterval);
	}

	private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

	public void Dispose() => _sweeper.Dispose();

	private void Sweep()
	{
		var now = _timeProvider.GetUtcNow();
		foreach (var (key, entry) in _entries)
		{
			if (now - entry.CreatedAt >= MaxAge)
			{
				_entries.TryRemove(key, out _);
			}
		}
	}

	// 창이 길수록 절감이 크고 유출 시 노출 범위도 크다. 편집이 몰리는 구간(연속 저장)만
	// 흡수하는 것이 목적이라 짧게 잡는다.
	public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

	// "재사용" 횟수의 상한이다 - 키를 만든 저장 1회가 앞에 있으므로, 한 데이터 키가 보호하는
	// 번들 수는 최대 MaxReuses + 1이다.
	public const Int32 MaxReuses = 50;

	private sealed class Entry(SopsWrappedDataKey wrapped, DateTimeOffset createdAt)
	{
		public SopsWrappedDataKey Wrapped { get; } = wrapped;
		public DateTimeOffset CreatedAt { get; } = createdAt;
		public Int32 Uses;
	}

	private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

	public bool TryGet(string appName, string adminCmkArn, string appCmkArn, out SopsWrappedDataKey wrapped)
	{
		var key = BuildKey(appName, adminCmkArn, appCmkArn);
		if (_entries.TryGetValue(key, out var entry) &&
			_timeProvider.GetUtcNow() - entry.CreatedAt < MaxAge &&
			Interlocked.Increment(ref entry.Uses) <= MaxReuses)
		{
			_metrics?.RecordDataKeyReuseHit();
			wrapped = entry.Wrapped;
			return true;
		}

		// 만료됐거나 사용 한도를 넘겼으면 버린다 - 다음 저장이 새 키를 만들어 다시 채운다.
		_entries.TryRemove(key, out _);
		_metrics?.RecordDataKeyReuseMiss();
		wrapped = null!;
		return false;
	}

	public void Set(string appName, string adminCmkArn, string appCmkArn, SopsWrappedDataKey wrapped) =>
		_entries[BuildKey(appName, adminCmkArn, appCmkArn)] =
			new Entry(wrapped, _timeProvider.GetUtcNow());

	// appName에 구분자가 섞여도 다른 조합과 충돌하지 않도록 길이를 함께 넣는다.
	private static string BuildKey(string appName, string adminCmkArn, string appCmkArn) =>
		$"{appName.Length}:{appName}\n{adminCmkArn}\n{appCmkArn}";
}

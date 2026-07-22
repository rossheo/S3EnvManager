using System.Collections.Concurrent;

namespace S3EnvManager.Web.Services;

public interface IBucketHealthStatusStore
{
	void Set(BucketHealthReport report);

	IReadOnlyCollection<BucketHealthReport> GetAll();
}

public sealed class BucketHealthStatusStore : IBucketHealthStatusStore
{
	private readonly ConcurrentDictionary<string, BucketHealthReport> _reports = new();

	public void Set(BucketHealthReport report) => _reports[report.Bucket] = report;

	public IReadOnlyCollection<BucketHealthReport> GetAll() => _reports.Values.OrderBy(r => r.Bucket).ToList();
}
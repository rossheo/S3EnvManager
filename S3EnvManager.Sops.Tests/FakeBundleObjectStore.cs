using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using S3EnvManager.Cli;

namespace S3EnvManager.Sops.Tests;

/// <summary>테스트용 인메모리 <see cref="IBundleObjectStore"/> - 실제 S3 호출 없이
/// BundleFetcher의 병합 로직/예외→exit 코드 매핑을 검증한다.</summary>
public sealed class FakeBundleObjectStore : IBundleObjectStore
{
	private readonly Dictionary<string, string> _objects = [];
	private readonly HashSet<string> _forbiddenKeys = [];
	private readonly HashSet<string> _serviceUnavailableKeys = [];

	public void SetObject(string key, string content) => _objects[key] = content;

	public void SetForbidden(string key) => _forbiddenKeys.Add(key);

	/// <summary>403/404가 아닌 나머지 S3 실패(5xx 등)를 재현한다 - exit 6 매핑 검증용.</summary>
	public void SetServiceUnavailable(string key) => _serviceUnavailableKeys.Add(key);

	public Task<string> GetObjectContentAsync(string bucket, string key, CancellationToken cancellationToken)
	{
		if (_forbiddenKeys.Contains(key))
		{
			throw new AmazonS3Exception(
				"가짜 AccessDenied", ErrorType.Sender, "AccessDenied", "req-id", HttpStatusCode.Forbidden);
		}
		if (_serviceUnavailableKeys.Contains(key))
		{
			throw new AmazonS3Exception(
				"가짜 ServiceUnavailable", ErrorType.Receiver, "ServiceUnavailable", "req-id",
				HttpStatusCode.ServiceUnavailable);
		}
		if (!_objects.TryGetValue(key, out var content))
		{
			throw new AmazonS3Exception(
				"가짜 NoSuchKey", ErrorType.Sender, "NoSuchKey", "req-id", HttpStatusCode.NotFound);
		}
		return Task.FromResult(content);
	}
}

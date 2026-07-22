using System.Security.Cryptography;
using System.Text;
using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

/// <summary>실 S3 없이 (bucket, key)별 버전 목록을 인메모리로 흉내낸다.</summary>
public sealed class FakeSecretObjectStore : ISecretObjectStore
{
	private sealed class VersionEntry
	{
		public required string VersionId;
		public required string Content;
		public required string ETag;
		public string? ActorEmail;
		public DateTimeOffset LastModified;
	}

	private readonly Dictionary<(string Bucket, string Key), List<VersionEntry>> objects = [];

	private List<VersionEntry> VersionsFor(string bucket, string key)
	{
		var location = (bucket, key);
		if (!objects.TryGetValue(location, out var list))
		{
			list = [];
			objects[location] = list;
		}
		return list;
	}

	public Task<StoredSecretObject?> GetCurrentAsync(
		string bucket, string key, CancellationToken cancellationToken = default)
	{
		var versions = VersionsFor(bucket, key);
		if (versions.Count == 0)
		{
			return Task.FromResult<StoredSecretObject?>(null);
		}
		var latest = versions[^1];
		return Task.FromResult<StoredSecretObject?>(new StoredSecretObject(latest.Content, latest.ETag, latest.VersionId));
	}

	public Task<PutSecretObjectResult> PutAsync(
		string bucket, string key, string content, string? actorEmail = null,
		CancellationToken cancellationToken = default)
	{
		var entry = new VersionEntry
		{
			VersionId = "v" + Guid.NewGuid().ToString("N"),
			Content = content,
			ETag = ComputeETag(content),
			ActorEmail = actorEmail,
			LastModified = DateTimeOffset.UtcNow,
		};
		VersionsFor(bucket, key).Add(entry);
		return Task.FromResult(new PutSecretObjectResult(entry.ETag, entry.VersionId));
	}

	public Task RestoreVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default)
	{
		var versions = VersionsFor(bucket, key);
		var source = versions.Single(v => v.VersionId == versionId);
		versions.Add(new VersionEntry
		{
			VersionId = "v" + Guid.NewGuid().ToString("N"),
			Content = source.Content,
			// 실 S3 ETag는 콘텐츠의 MD5라 복원해도 원본과 같아진다 - 순번 기반이면 이 동일성이 깨진다.
			ETag = ComputeETag(source.Content),
			ActorEmail = source.ActorEmail,
			LastModified = DateTimeOffset.UtcNow,
		});
		return Task.CompletedTask;
	}

	private static string ComputeETag(string content) =>
		Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(content)));

	public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default)
	{
		VersionsFor(bucket, key).Clear();
		return Task.CompletedTask;
	}

	public Task<List<SecretObjectVersion>> ListVersionsAsync(
		string bucket, string key, bool includeActorEmail = false, CancellationToken cancellationToken = default)
	{
		var versions = VersionsFor(bucket, key);
		var result = versions.Select((v, i) => new SecretObjectVersion(
			v.VersionId, i == versions.Count - 1, v.LastModified, includeActorEmail ? v.ActorEmail : null))
			.ToList();
		return Task.FromResult(result);
	}

	public Task<string> GetVersionContentAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default)
	{
		var entry = VersionsFor(bucket, key).Single(v => v.VersionId == versionId);
		return Task.FromResult(entry.Content);
	}

	public Task DeleteVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default)
	{
		VersionsFor(bucket, key).RemoveAll(v => v.VersionId == versionId);
		return Task.CompletedTask;
	}
}

using Amazon.S3;
using Amazon.S3.Model;

namespace S3EnvManager.Web.Services;

public sealed class S3SecretObjectStore(IAmazonS3ClientProvider s3ClientProvider) : ISecretObjectStore
{
	private IAmazonS3 s3Client => s3ClientProvider.GetClient();


	public async Task<StoredSecretObject?> GetCurrentAsync(
		string bucket, string key, CancellationToken cancellationToken = default)
	{
		try
		{
			using var response = await s3Client.GetObjectAsync(
				new GetObjectRequest { BucketName = bucket, Key = key }, cancellationToken)
				.ConfigureAwait(false);
			using var reader = new StreamReader(response.ResponseStream);
			var content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
			return new StoredSecretObject(content, Unquote(response.ETag), response.VersionId);
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async Task<PutSecretObjectResult> PutAsync(
		string bucket, string key, string content, string? actorEmail = null,
		CancellationToken cancellationToken = default)
	{
		var request = new PutObjectRequest
		{
			BucketName = bucket,
			Key = key,
			ContentBody = content,
		};
		if (actorEmail is not null)
		{
			request.Metadata["actor-email"] = actorEmail;
		}

		var response = await s3Client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

		return new PutSecretObjectResult(Unquote(response.ETag), response.VersionId);
	}

	public Task RestoreVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
		s3Client.CopyObjectAsync(new CopyObjectRequest
		{
			SourceBucket = bucket,
			SourceKey = key,
			SourceVersionId = versionId,
			DestinationBucket = bucket,
			DestinationKey = key,
			// 소스/목적지 키가 같으면 S3가 "자기 자신에 대한 복사"로 보고 거부하므로
			// MetadataDirective를 REPLACE로 강제해야 실제로 복사가 실행된다.
			MetadataDirective = S3MetadataDirective.REPLACE,
		}, cancellationToken);

	public Task DeleteAsync(string bucket, string key, CancellationToken cancellationToken = default) =>
		s3Client.DeleteObjectAsync(bucket, key, cancellationToken);

	public async Task<List<SecretObjectVersion>> ListVersionsAsync(
		string bucket, string key, bool includeActorEmail = false, CancellationToken cancellationToken = default)
	{
		var response = await s3Client.ListVersionsAsync(
			new ListVersionsRequest { BucketName = bucket, Prefix = key }, cancellationToken)
			.ConfigureAwait(false);
		// 매칭 오브젝트가 없으면 S3가 빈 리스트가 아니라 Versions 자체를 null로 돌려준다.
		var matching = (response.Versions ?? [])
			.Where(v => v.Key == key && v.IsDeleteMarker != true)
			.ToList();

		var versions = new List<SecretObjectVersion>(matching.Count);
		foreach (var v in matching)
		{
			// ListVersions 응답에는 커스텀 메타데이터가 없어 버전별 HEAD 요청이 추가로 필요하다.
			var actorEmail = includeActorEmail
				? await GetActorEmailAsync(bucket, key, v.VersionId, cancellationToken).ConfigureAwait(false)
				: null;
			versions.Add(new SecretObjectVersion(
				v.VersionId, v.IsLatest ?? false,
				new DateTimeOffset(DateTime.SpecifyKind(v.LastModified!.Value, DateTimeKind.Utc)), actorEmail));
		}
		return versions;
	}

	private async Task<string?> GetActorEmailAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken)
	{
		try
		{
			var metadata = await s3Client.GetObjectMetadataAsync(
				new GetObjectMetadataRequest { BucketName = bucket, Key = key, VersionId = versionId }, cancellationToken)
				.ConfigureAwait(false);
			// MetadataCollection 인덱서는 키가 없으면 예외 대신 null을 돌려준다(AWSSDK.S3 동작).
			return metadata.Metadata["actor-email"];
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
		{
			return null;
		}
	}

	public async Task<string> GetVersionContentAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default)
	{
		using var response = await s3Client.GetObjectAsync(
			new GetObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }, cancellationToken)
			.ConfigureAwait(false);
		using var reader = new StreamReader(response.ResponseStream);
		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	public Task DeleteVersionAsync(
		string bucket, string key, string versionId, CancellationToken cancellationToken = default) =>
		s3Client.DeleteObjectAsync(
			new DeleteObjectRequest { BucketName = bucket, Key = key, VersionId = versionId }, cancellationToken);

	private static string Unquote(string eTag) => eTag.Trim('"');
}
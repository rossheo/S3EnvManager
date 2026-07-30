using Amazon.S3;

namespace S3EnvManager.Cli;

/// <summary>실제 S3를 호출하는 <see cref="IBundleObjectStore"/> 구현.</summary>
public sealed class AwsBundleObjectStore(IAmazonS3 s3) : IBundleObjectStore
{
	public async Task<string> GetObjectContentAsync(
		string bucket, string key, CancellationToken cancellationToken)
	{
		using var response = await s3.GetObjectAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		using var reader = new StreamReader(response.ResponseStream);
		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}
}

using Microsoft.Extensions.Configuration;

namespace S3EnvManager.Configuration;

public sealed class S3EnvManagerConfigurationSource(S3EnvManagerConfigurationOptions options)
	: IConfigurationSource
{
	public IConfigurationProvider Build(IConfigurationBuilder builder) =>
		new S3EnvManagerConfigurationProvider(options);
}
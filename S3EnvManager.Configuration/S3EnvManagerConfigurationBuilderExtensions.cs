using Microsoft.Extensions.Configuration;

namespace S3EnvManager.Configuration;

public static class S3EnvManagerConfigurationBuilderExtensions
{
	/// <summary>`AddJsonFile`/`AddEnvironmentVariables`와 동일한 방식으로 S3EnvManager가
	/// 관리하는 시크릿 번들을 설정 소스로 추가한다.</summary>
	public static IConfigurationBuilder AddS3EnvManager(
		this IConfigurationBuilder builder, Action<S3EnvManagerConfigurationOptions> configure)
	{
		var options = new S3EnvManagerConfigurationOptions
		{
			Bucket = string.Empty,
			AppName = string.Empty,
			EnvSegment = string.Empty,
		};
		configure(options);
		return builder.Add(new S3EnvManagerConfigurationSource(options));
	}
}
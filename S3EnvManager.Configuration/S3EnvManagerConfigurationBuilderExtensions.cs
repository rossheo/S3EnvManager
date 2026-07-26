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
		// OptionalIfMissing 기본값(true)과 맞물려, OnDiagnostic을 연결하지 않으면 자격증명/버킷
		// 접근 실패가 완전히 조용히 넘어가 "번들이 로드된 줄 알았는데 실제로는 빈 값" 사고로
		// 이어지기 쉽다(StockTrade apiservice 배포에서 실제 발생). 명시적으로 설정하지 않은
		// 경우에만 stderr 출력으로 기본 가시성을 보장한다.
		options.OnDiagnostic ??= (level, message, exception) =>
			Console.Error.WriteLine($"[S3EnvManager:{level}] {message}{(exception is null ? string.Empty : $" -> {exception}")}");
		return builder.Add(new S3EnvManagerConfigurationSource(options));
	}
}
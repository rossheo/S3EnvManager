using Amazon.Runtime;

namespace S3EnvManager.Configuration;

public enum S3EnvManagerLogLevel
{
	Warning,
	Error,
}

/// <summary>Application 측 IConfiguration Provider 설정.</summary>
public sealed class S3EnvManagerConfigurationOptions
{
	/// <summary>S3EnvManager에서 App 등록 시 지정한 버킷.</summary>
	public required string Bucket { get; set; }

	/// <summary>오브젝트 경로 `{app}/{env}.env`의 `{app}` 부분.</summary>
	public required string AppName { get; set; }

	/// <summary>오브젝트 경로의 `{env}` 부분 - "dev"/"staging"/"product".</summary>
	public required string EnvSegment { get; set; }

	/// <summary>true면 overwrite 번들(`{app}/{env}.overwrite.env`)을 읽는다. 기본 false(base
	/// 번들). 3단 우선순위(base → env var → overwrite)는 이 Provider를 두 번 등록해서 만든다.</summary>
	public bool IsOverwriteBundle { get; set; }

	/// <summary>폴링 주기.</summary>
	public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>암호문(sops로 암호화된 원본, 평문 아님)을 캐싱해둘 로컬 파일 경로.
	/// 지정하지 않으면 임시 디렉터리 아래 앱/환경별 파일을 사용한다.</summary>
	public string? LocalCacheFilePath { get; set; }

	/// <summary>오브젝트가 아예 없을 때(아직 한 번도 저장 안 된 base 번들) 예외 대신 빈
	/// 값으로 취급할지. 기본 true.</summary>
	public bool OptionalIfMissing { get; set; } = true;

	/// <summary>AWS 리전 이름(예: "us-east-1"). AWSSDK의 `AuthenticationRegion`으로 그대로
	/// 전달된다.</summary>
	public string? Region { get; set; }

	/// <summary>지정하지 않으면 AWS SDK 기본 자격증명 체인(환경변수 등)을 그대로 쓴다.</summary>
	public AWSCredentials? Credentials { get; set; }

	/// <summary>진단 메시지 콜백 - 별도 로깅 프레임워크에 의존하지 않기 위해 단순 델리게이트로
	/// 둔다("최소 의존성" 원칙).</summary>
	public Action<S3EnvManagerLogLevel, string, Exception?>? OnDiagnostic { get; set; }
}
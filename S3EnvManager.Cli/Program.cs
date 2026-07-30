using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using S3EnvManager.Sops;

namespace S3EnvManager.Cli;

public static class Program
{
	private const string HelpText = """
		S3EnvManager.Cli - S3EnvManager 시크릿 번들 읽기 전용 조회 도구

		사용법:
		  s3envmanager get     --key <KEY> [--allow-missing] [--allow-missing-bundle]
		                       [--bucket <BUCKET>] [--app <APP>] [--env <ENV>] [--region <REGION>]
		  s3envmanager get-all [--format json|dotenv] [--allow-missing-bundle]
		                       [--bucket <BUCKET>] [--app <APP>] [--env <ENV>] [--region <REGION>]
		  s3envmanager --help | -h
		  s3envmanager --version | -v

		--bucket/--app/--env/--region은 인자 대신 환경변수로도 지정할 수 있습니다:
		  S3ENVMANAGER_BUCKET, S3ENVMANAGER_APP_NAME, S3ENVMANAGER_ENV_SEGMENT, AWS_REGION

		  --bucket <BUCKET>  S3 버킷 이름(예: my-bucket)
		  --app <APP>        S3EnvManager에 등록된 App 이름(예: S3EnvManager)
		  --env <ENV>        dev, staging, product 중 하나
		  --region <REGION>  AWS 리전(예: ap-northeast-2) - 버킷/CMK가 있는 리전과 일치해야 함

		예시:
		  s3envmanager get-all --bucket my-bucket --app S3EnvManager --env product --region ap-northeast-2
		  s3envmanager get --key ConnectionStrings__s3envmanagerdb --bucket my-bucket --app S3EnvManager --env product --region ap-northeast-2

		AWS 자격증명은 SDK 기본 자격증명 체인(환경변수, 공유 credentials 파일, 인스턴스
		프로필 등)을 따릅니다. get-all은 base({app}/{env}.env)와 overwrite
		({app}/{env}.overwrite.env) 번들을 병합한 유효값을 반환합니다.

		종료 코드:
		  0 성공                       5 get에서 지정한 키가 없음
		  1 인자/설정 오류              6 그 외 AWS 호출 실패(일시적일 수 있음)
		  2 AWS 인증/권한 오류          7 dotenv로 표현할 수 없는 값(개행 포함)
		  3 번들 자체가 없음            8 예상하지 못한 내부 오류
		  4 복호화/무결성 실패(변조 의심)

		""";

	public static async Task<Int32> Main(string[] args)
	{
		if (args.Any(a => a is "--help" or "-h"))
		{
			Console.Out.Write(HelpText);
			return (Int32)ExitCode.Success;
		}
		if (args.Any(a => a is "--version" or "-v"))
		{
			Console.Out.Write(VersionInfo.Version);
			Console.Out.Write('\n');
			return (Int32)ExitCode.Success;
		}

		try
		{
			var options = CliOptions.Parse(args);
			using var s3Client = BuildS3Client(options.Region);
			using var kmsClient = BuildKmsClient(options.Region);
			var fetcher = new BundleFetcher(new AwsBundleObjectStore(s3Client), new AwsKmsKeyOperations(kmsClient));

			return options.Command switch
			{
				CliCommand.Get => await RunGetAsync(fetcher, options).ConfigureAwait(false),
				CliCommand.GetAll => await RunGetAllAsync(fetcher, options).ConfigureAwait(false),
				_ => throw new InvalidOperationException($"처리되지 않은 명령: {options.Command}"),
			};
		}
		catch (CliException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return (Int32)ex.ExitCode;
		}
		catch (Exception ex)
		{
			// 위 두 메서드가 던지는 CliException 외의 처리되지 않은 예외(예상 못한
			// 버그) - 배포 스크립트 로그에 스택트레이스를 그대로 흘리는 대신 메시지만
			// 남기고 exit 8로 실패한다. ArgumentError(1)를 재사용하지 않는다 - 호출자가
			// "인자를 다시 확인하면 되는 문제"로 오해하면 안 되기 때문이다.
			Console.Error.WriteLine($"예상하지 못한 오류가 발생했습니다: {ex.Message}");
			return (Int32)ExitCode.UnexpectedError;
		}
	}

	private static async Task<Int32> RunGetAsync(BundleFetcher fetcher, CliOptions options)
	{
		// get의 --allow-missing은 "번들 자체가 없는 경우"도 함께 허용한다 - 호출자가
		// 이미 "이 키에 값이 없어도 괜찮다"고 명시했으므로, 번들이 통째로 없어서 못 찾은
		// 것과 번들 안에 그 키만 없어서 못 찾은 것을 구분할 이유가 없다(get-all과 달리
		// get은 "전체 그림"을 요구하지 않는다).
		var allowMissingBundle = options.AllowMissingBundle || options.AllowMissing;
		var result = await fetcher.FetchAsync(
			options.Bucket, options.AppName, options.EnvSegment, allowMissingBundle, CancellationToken.None)
			.ConfigureAwait(false);
		WriteWarningIfAny(result);

		if (!result.Values.TryGetValue(options.Key!, out var value))
		{
			if (options.AllowMissing)
			{
				Console.Out.Write('\n');
				return (Int32)ExitCode.Success;
			}
			throw new CliException(ExitCode.KeyMissing, $"키 '{options.Key}'가 번들에 없습니다.");
		}

		// Console.WriteLine은 Windows에서 Environment.NewLine("\r\n")을 쓴다 - 이 값이
		// 그대로 k8s Secret 등에 들어가면 "\r"가 섞여 들어가는 조용한 손상이 될 수 있으므로,
		// 개행은 항상 "\n" 하나로 고정한다.
		Console.Out.Write(value);
		Console.Out.Write('\n');
		return (Int32)ExitCode.Success;
	}

	private static async Task<Int32> RunGetAllAsync(BundleFetcher fetcher, CliOptions options)
	{
		var result = await fetcher.FetchAsync(
			options.Bucket, options.AppName, options.EnvSegment, options.AllowMissingBundle, CancellationToken.None)
			.ConfigureAwait(false);
		WriteWarningIfAny(result);

		Console.Write(OutputFormatter.FormatGetAll(result.Values, options.Format));
		return (Int32)ExitCode.Success;
	}

	private static void WriteWarningIfAny(BundleFetchResult result)
	{
		if (result.Warning is { Length: > 0 } warning)
		{
			Console.Error.WriteLine(warning);
		}
	}

	private static AmazonS3Client BuildS3Client(string? region)
	{
		var config = new AmazonS3Config();
		if (region is not null)
		{
			// RegionEndpoint는 정상적인 AWS 엔드포인트 해석에, AuthenticationRegion은 SigV4
			// 서명에 쓰인다 - S3EnvManagerConfigurationProvider와 동일한 이유.
			config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
			config.AuthenticationRegion = region;
		}
		return new AmazonS3Client(config);
	}

	private static AmazonKeyManagementServiceClient BuildKmsClient(string? region)
	{
		var config = new AmazonKeyManagementServiceConfig();
		if (region is not null)
		{
			config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
			config.AuthenticationRegion = region;
		}
		return new AmazonKeyManagementServiceClient(config);
	}
}

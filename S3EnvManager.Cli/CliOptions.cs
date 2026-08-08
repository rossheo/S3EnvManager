namespace S3EnvManager.Cli;

public enum CliCommand
{
	Get,
	GetAll,
}

public enum OutputFormat
{
	Json,
	Dotenv,
}

public sealed class CliOptions
{
	public required CliCommand Command { get; init; }
	public required string Bucket { get; init; }
	public required string AppName { get; init; }
	public required string EnvSegment { get; init; }
	public string? Region { get; init; }

	/// <summary>get 명령이 요청한 키들. --key를 여러 번 줄 수 있다 - 번들 하나를 받아
	/// 한 번만 복호화하므로, 키 N개를 get으로 N번 부르는 것보다 KMS Decrypt 호출이
	/// 1/N로 줄어든다(호출 1회당 base+overwrite 2회를 쓴다). 순서는 준 순서를 보존한다.</summary>
	public IReadOnlyList<string> Keys { get; init; } = [];

	public bool AllowMissing { get; init; }
	public bool AllowMissingBundle { get; init; }
	public OutputFormat Format { get; init; } = OutputFormat.Json;

	/// <summary>인자를 우선하고, 없으면 stocktrade의 AddS3EnvManagerExtension.cs가 이미
	/// 쓰는 환경변수 이름(S3ENVMANAGER_BUCKET/_APP_NAME/_ENV_SEGMENT, AWS_REGION)으로
	/// 대체한다 - k8s Secret에 이미들어있는 값과 CLI 호출이 같은 이름 규칙을 공유하게
	/// 하기 위함(PLAN-cli.md "명령어 설계" 절 참고). <paramref name="getEnvironmentVariable"/>는
	/// 테스트가 실제 프로세스 환경변수와 무관하게 결정론적으로 검증할 수 있도록 주입
	/// 지점을 남긴 것 - 기본값은 <see cref="Environment.GetEnvironmentVariable(string)"/>.</summary>
	public static CliOptions Parse(string[] args, Func<string, string?>? getEnvironmentVariable = null)
	{
		var getEnv = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

		if (args.Length == 0)
		{
			throw new CliException(ExitCode.ArgumentError, "명령을 지정하세요: get 또는 get-all.");
		}

		var command = args[0] switch
		{
			"get" => CliCommand.Get,
			"get-all" => CliCommand.GetAll,
			var other => throw new CliException(
				ExitCode.ArgumentError, $"알 수 없는 명령입니다: '{other}' (get 또는 get-all만 지원)."),
		};

		string? bucket = null;
		string? appName = null;
		string? envSegment = null;
		string? region = null;
		var keys = new List<string>();
		var allowMissing = false;
		var allowMissingBundle = false;
		var format = OutputFormat.Json;

		for (var i = 1; i < args.Length; i++)
		{
			switch (args[i])
			{
				case "--bucket":
					bucket = RequireValue(args, ref i, "--bucket");
					break;
				case "--app":
					appName = RequireValue(args, ref i, "--app");
					break;
				case "--env":
					envSegment = RequireValue(args, ref i, "--env");
					break;
				case "--region":
					region = RequireValue(args, ref i, "--region");
					break;
				case "--key":
					keys.Add(RequireValue(args, ref i, "--key"));
					break;
				case "--allow-missing":
					allowMissing = true;
					break;
				case "--allow-missing-bundle":
					allowMissingBundle = true;
					break;
				case "--format":
					var formatValue = RequireValue(args, ref i, "--format");
					format = formatValue switch
					{
						"json" => OutputFormat.Json,
						"dotenv" => OutputFormat.Dotenv,
						_ => throw new CliException(
							ExitCode.ArgumentError, $"알 수 없는 --format 값입니다: '{formatValue}' (json 또는 dotenv)."),
					};
					break;
				default:
					throw new CliException(ExitCode.ArgumentError, $"알 수 없는 인자입니다: '{args[i]}'.");
			}
		}

		bucket ??= getEnv("S3ENVMANAGER_BUCKET");
		appName ??= getEnv("S3ENVMANAGER_APP_NAME");
		envSegment ??= getEnv("S3ENVMANAGER_ENV_SEGMENT");
		region ??= getEnv("AWS_REGION");

		if (string.IsNullOrWhiteSpace(bucket) || string.IsNullOrWhiteSpace(appName)
			|| string.IsNullOrWhiteSpace(envSegment))
		{
			throw new CliException(
				ExitCode.ArgumentError,
				"--bucket/--app/--env(또는 S3ENVMANAGER_BUCKET/_APP_NAME/_ENV_SEGMENT)를 모두 지정하세요.");
		}

		if (command == CliCommand.Get && keys.Count == 0)
		{
			throw new CliException(ExitCode.ArgumentError, "get 명령에는 --key가 필요합니다.");
		}
		// `--key "$MYVAR"`에서 변수가 비어 있는 흔한 실수를 인자 오류(1)로 잡는다 - 빈 키를
		// 그대로 통과시키면 "번들에 그 키가 없음"(5)이나 --allow-missing과 함께 0으로 끝나
		// 호출자가 설정 실수를 알아채지 못한다.
		if (keys.Any(string.IsNullOrWhiteSpace))
		{
			throw new CliException(ExitCode.ArgumentError, "--key에 빈 값을 줄 수 없습니다.");
		}
		if (command == CliCommand.GetAll && keys.Count > 0)
		{
			throw new CliException(ExitCode.ArgumentError, "get-all은 --key를 받지 않습니다 - get을 쓰세요.");
		}

		var duplicateKey = keys.GroupBy(k => k, StringComparer.Ordinal)
			.FirstOrDefault(g => g.Count() > 1)?.Key;
		if (duplicateKey is not null)
		{
			throw new CliException(ExitCode.ArgumentError, $"--key가 중복 지정되었습니다: '{duplicateKey}'.");
		}

		return new CliOptions
		{
			Command = command,
			Bucket = bucket,
			AppName = appName,
			EnvSegment = envSegment,
			Region = region,
			Keys = keys,
			AllowMissing = allowMissing,
			AllowMissingBundle = allowMissingBundle,
			Format = format,
		};
	}

	private static string RequireValue(string[] args, ref Int32 i, string flagName)
	{
		if (i + 1 >= args.Length)
		{
			throw new CliException(ExitCode.ArgumentError, $"{flagName} 뒤에 값이 필요합니다.");
		}
		i++;
		return args[i];
	}
}

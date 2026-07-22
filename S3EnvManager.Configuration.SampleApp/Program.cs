using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using S3EnvManager.Configuration;

// S3EnvManager.Configuration을 참조하는 제3자 Application을 흉내 낸 샘플.
var region = Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")
	?? Environment.GetEnvironmentVariable("AWS_REGION");
var bucket = RequireEnv("S3ENVMANAGER_BUCKET");
var appName = RequireEnv("S3ENVMANAGER_APP_NAME");
var envSegment = RequireEnv("S3ENVMANAGER_ENV_SEGMENT");

var configuration = new ConfigurationBuilder()
	.AddS3EnvManager(options =>
	{
		if (region is not null)
		{
			options.Region = region;
		}
		options.Bucket = bucket;
		options.AppName = appName;
		options.EnvSegment = envSegment;
		options.PollInterval = TimeSpan.FromSeconds(5);
		options.OnDiagnostic = (level, message, exception) =>
			Console.WriteLine($"[{level}] {message}{(exception is null ? "" : $" ({exception.Message})")}");
	})
	.Build();

Console.WriteLine($"[SampleApp] {appName}/{envSegment} 설정을 불러왔습니다. 감시 중(5초 간격) - Ctrl+C로 종료.");
PrintAll(configuration);

ChangeToken.OnChange(configuration.GetReloadToken, () =>
{
	Console.WriteLine("[SampleApp] 변경 감지 - 값을 다시 읽습니다.");
	PrintAll(configuration);
});

await Task.Delay(Timeout.Infinite);

static void PrintAll(IConfiguration configuration)
{
	foreach (var kv in configuration.AsEnumerable().Where(kv => kv.Value is not null))
	{
		Console.WriteLine($"  {kv.Key} = {kv.Value}");
	}
}

static string RequireEnv(string name) =>
	Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"환경변수 {name}가 필요합니다.");
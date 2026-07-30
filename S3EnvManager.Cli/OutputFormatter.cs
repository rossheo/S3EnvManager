using System.Text;
using System.Text.Json;

namespace S3EnvManager.Cli;

/// <summary>get-all 출력 포맷. 기본값은 json이다 - PowerShell(deploy.ps1/deploy-all.ps1)이
/// 1차 소비자이고 값 자체가 connection string(`=`/`;` 포함)이라 dotenv를 기본값으로 두면
/// 손실 있는 경로가 기본이 된다(PLAN-cli.md "명령어 설계" 절 참고).</summary>
public static class OutputFormatter
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static string FormatGetAll(IReadOnlyDictionary<string, string> values, OutputFormat format) =>
		format switch
		{
			OutputFormat.Json => JsonSerializer.Serialize(values, JsonOptions),
			OutputFormat.Dotenv => FormatDotenv(values),
			_ => throw new ArgumentOutOfRangeException(nameof(format)),
		};

	private static string FormatDotenv(IReadOnlyDictionary<string, string> values)
	{
		var builder = new StringBuilder();
		foreach (var (key, value) in values)
		{
			if (value.Contains('\n') || value.Contains('\r'))
			{
				// dotenv(KEY=VALUE\n...)는 값의 개행을 표현할 방법이 없다 - 조용히 잘라내는
				// 대신 실패시켜 호출자가 --format json으로 바꾸게 한다.
				throw new CliException(
					ExitCode.OutputFormatError,
					$"'{key}' 값에 개행이 포함돼 있어 dotenv 포맷으로 표현할 수 없습니다 - --format json을 쓰세요.");
			}
			// 첫 '='만 키/값 구분자로 쓰고, 값 안의 '='는 그대로 남긴다(KEY=A=B 형태의
			// 값도 안전하게 표현) - 이 CLI는 쓰는 쪽만 하므로 파싱 규칙은 소비자 몫이다.
			builder.Append(key).Append('=').Append(value).Append('\n');
		}
		return builder.ToString();
	}
}

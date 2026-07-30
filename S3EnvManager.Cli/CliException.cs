namespace S3EnvManager.Cli;

/// <summary>사용자에게 보여줄 메시지와 종료 코드를 함께 나르는 예외 - Program.Main이 이것만
/// 캐치해서 Console.Error에 메시지를 쓰고 ExitCode를 그대로 프로세스 종료 코드로 반환한다.</summary>
public sealed class CliException(ExitCode exitCode, string message, Exception? innerException = null)
	: Exception(message, innerException)
{
	public ExitCode ExitCode { get; } = exitCode;
}

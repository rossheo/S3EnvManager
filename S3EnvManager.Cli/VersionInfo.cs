using System.Reflection;

namespace S3EnvManager.Cli;

/// <summary>빌드 시점의 어셈블리 버전(솔루션 루트 Directory.Build.targets의 GitInformation
/// 타겟이 심어둔 값). S3EnvManager.Web/VersionInfo.cs와 같은 방식으로 읽는다.</summary>
public static class VersionInfo
{
	public static string Version { get; }

	static VersionInfo()
	{
		var informationalVersion = Assembly.GetExecutingAssembly()
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		Version = informationalVersion?.Split('+')[0] is { Length: > 0 } v ? v : "0.0.0";
	}
}

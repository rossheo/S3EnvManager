using System.Reflection;

namespace S3EnvManager.Web;

/// <summary>빌드 시점의 어셈블리 버전과 git 브랜치/커밋 해시(솔루션 루트 Directory.Build.targets의
/// GitInformation 타겟이 AssemblyMetadata로 심어둔 값)를 읽어 화면(NavMenu 하단)에 표시한다.</summary>
public static class VersionInfo
{
	public static string Version { get; }

	public static string GitBranch { get; }

	public static string GitCommitHash { get; }

	static VersionInfo()
	{
		var assembly = Assembly.GetExecutingAssembly();

		var informationalVersion =
			assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
		Version = informationalVersion?.Split('+')[0] is { Length: > 0 } v ? v : "0.0.0";

		var metadata = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
			.Where(m => m.Key is not null && m.Value is not null)
			.ToDictionary(m => m.Key!, m => m.Value!);

		GitBranch = metadata.TryGetValue("GitBranch", out var branch) && !string.IsNullOrWhiteSpace(branch)
			? branch : "unknown";
		GitCommitHash = metadata.TryGetValue("GitCommitHash", out var hash) && !string.IsNullOrWhiteSpace(hash)
			? hash : "unknown";
	}
}
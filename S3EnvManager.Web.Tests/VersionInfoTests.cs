using S3EnvManager.Web;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>Directory.Build.targets의 GitInformation 타겟이 git 브랜치/커밋 해시를 심었는지 확인한다.</summary>
public class VersionInfoTests
{
	[Fact]
	public void GitBranchAndCommitHash_AreEmbedded_NotUnknown()
	{
		Assert.NotEqual("unknown", VersionInfo.GitBranch);
		Assert.NotEqual("unknown", VersionInfo.GitCommitHash);
		Assert.Matches("^[0-9a-f]{7,40}$", VersionInfo.GitCommitHash);
	}
}
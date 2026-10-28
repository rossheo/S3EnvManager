using S3EnvManager.Sops;
using S3EnvManager.Web.Services;

namespace S3EnvManager.Web.Tests;

public class SecretCommentsTests
{
	[Fact]
	public void Split_AnchorsCommentsToFollowingKey_AndTrailingToNull()
	{
		var (values, comments) = SecretComments.Split(
		[
			SopsDotEnvLine.Comment(" head"),
			SopsDotEnvLine.Entry("A", "1"),
			SopsDotEnvLine.Comment(" b1"),
			SopsDotEnvLine.Comment(" b2"),
			SopsDotEnvLine.Entry("B", "2"),
			SopsDotEnvLine.Comment(" tail"),
		]);

		Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" }, values);
		Assert.Equal(
			[
				new SecretComment("A", " head"),
				new SecretComment("B", " b1"),
				new SecretComment("B", " b2"),
				new SecretComment(null, " tail"),
			],
			comments);
	}

	[Fact]
	public void Compose_IsInverseOfSplit()
	{
		List<SopsDotEnvLine> lines =
		[
			SopsDotEnvLine.Comment(" head"),
			SopsDotEnvLine.Entry("A", "1"),
			SopsDotEnvLine.Comment(""),
			SopsDotEnvLine.Entry("B", "2"),
			SopsDotEnvLine.Comment(" tail"),
		];

		var (values, comments) = SecretComments.Split(lines);

		Assert.Equal(lines, SecretComments.Compose(values, comments));
	}

	// 공유 시크릿 참조 키는 저장 시 맨 뒤로 재배치된다 - 주석은 위치가 아니라 키를 따라가야 한다.
	[Fact]
	public void Compose_CommentFollowsItsKeyWhenKeyIsReordered()
	{
		var values = new Dictionary<string, string> { ["B"] = "2", ["A"] = "1" };

		var lines = SecretComments.Compose(values, [new SecretComment("A", " about A")]);

		Assert.Equal(
			[
				SopsDotEnvLine.Entry("B", "2"),
				SopsDotEnvLine.Comment(" about A"),
				SopsDotEnvLine.Entry("A", "1"),
			],
			lines);
	}

	[Fact]
	public void Compose_CommentOfRemovedKey_MovesToEnd()
	{
		var values = new Dictionary<string, string> { ["B"] = "2" };

		var lines = SecretComments.Compose(
			values, [new SecretComment("GONE", " orphan"), new SecretComment(null, " tail")]);

		Assert.Equal(
			[
				SopsDotEnvLine.Entry("B", "2"),
				SopsDotEnvLine.Comment(" orphan"),
				SopsDotEnvLine.Comment(" tail"),
			],
			lines);
	}

	[Fact]
	public void Merge_OnlyTheirsChanged_TakesTheirs()
	{
		IReadOnlyList<SecretComment> baseComments = [new SecretComment(null, " v1")];
		IReadOnlyList<SecretComment> theirs = [new SecretComment(null, " v2")];

		Assert.Equal(theirs, SecretComments.Merge(baseComments, baseComments, theirs));
	}

	[Fact]
	public void Merge_BothChanged_MineWins()
	{
		IReadOnlyList<SecretComment> baseComments = [new SecretComment(null, " v1")];
		IReadOnlyList<SecretComment> mine = [new SecretComment(null, " mine")];
		IReadOnlyList<SecretComment> theirs = [new SecretComment(null, " theirs")];

		Assert.Equal(mine, SecretComments.Merge(baseComments, mine, theirs));
	}

	[Fact]
	public void Validate_RejectsNewline()
	{
		Assert.NotNull(SecretComments.Validate([new SecretComment(null, "a\nB=injected")]));
		Assert.Null(SecretComments.Validate([new SecretComment(null, " fine")]));
	}
}

using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// 주석은 위치가 의미이므로 "바로 다음에 오는 키"에 붙여 다룬다(BeforeKey가 null이면 파일 끝).
// 값/만료일/참조가 모두 키 단위 Dictionary로 흐르므로, 주석도 키에 매달아야 공유 시크릿 참조
// 키가 뒤로 재배치되는 저장 경로에서도 원래 앞에 있던 키를 따라간다.
public sealed record SecretComment(string? BeforeKey, string Text);

public static class SecretComments
{
	public static IReadOnlyList<SecretComment> Empty { get; } = [];

	// 줄 목록을 값과 주석으로 나눈다. 키 앞의 주석은 그 키에, 마지막 키 뒤의 주석은 파일 끝(null)에 붙는다.
	public static (Dictionary<string, string> Values, List<SecretComment> Comments) Split(
		IEnumerable<SopsDotEnvLine> lines)
	{
		var values = new Dictionary<string, string>();
		var comments = new List<SecretComment>();
		var pending = new List<string>();
		foreach (var line in lines)
		{
			if (line.IsComment)
			{
				pending.Add(line.Value);
				continue;
			}

			comments.AddRange(pending.Select(text => new SecretComment(line.Key, text)));
			pending.Clear();
			values[line.Key!] = line.Value;
		}
		comments.AddRange(pending.Select(text => new SecretComment(null, text)));
		return (values, comments);
	}

	// Split의 역. values의 순서를 따르며, 존재하지 않는 키에 붙은 주석은 파일 끝으로 보낸다.
	public static List<SopsDotEnvLine> Compose(
		IEnumerable<KeyValuePair<string, string>> values, IReadOnlyList<SecretComment> comments)
	{
		var lines = new List<SopsDotEnvLine>();
		var placedKeys = new HashSet<string>(StringComparer.Ordinal);
		var commentsByKey = comments.Where(c => c.BeforeKey is not null)
			.ToLookup(c => c.BeforeKey!, StringComparer.Ordinal);
		foreach (var (key, value) in values)
		{
			lines.AddRange(commentsByKey[key].Select(c => SopsDotEnvLine.Comment(c.Text)));
			lines.Add(SopsDotEnvLine.Entry(key, value));
			placedKeys.Add(key);
		}
		lines.AddRange(comments
			.Where(c => c.BeforeKey is null || !placedKeys.Contains(c.BeforeKey))
			.Select(c => SopsDotEnvLine.Comment(c.Text)));
		return lines;
	}

	// dotenv는 줄 단위 포맷이라 개행이 들어간 주석은 저장하면 다른 줄(혹은 키=값)로 깨진다.
	public static string? Validate(IReadOnlyList<SecretComment> comments) =>
		comments.Any(c => c.Text.Contains('\n') || c.Text.Contains('\r'))
			? "주석에는 개행 문자를 넣을 수 없습니다."
			: null;

	// 주석 전체를 하나의 단위로 3-way 병합한다. 한쪽만 바꿨으면 그쪽을 따르고, 양쪽 다 다르게
	// 바꿨으면 내 쪽을 따른다(주석은 실행에 영향이 없어 값처럼 사용자에게 고르게 하지 않는다).
	public static IReadOnlyList<SecretComment> Merge(
		IReadOnlyList<SecretComment> baseComments,
		IReadOnlyList<SecretComment> mineComments,
		IReadOnlyList<SecretComment> theirsComments) =>
		mineComments.SequenceEqual(baseComments) ? theirsComments : mineComments;
}

namespace S3EnvManager.Sops;

/// <summary>
/// dotenv 파일의 한 줄. <see cref="Key"/>가 null이면 주석 줄이고, <see cref="Value"/>는 '#' 뒤의
/// 텍스트 전체다(sops처럼 '#' 바로 뒤 공백도 그대로 보존한다).
/// </summary>
public sealed record SopsDotEnvLine(string? Key, string Value)
{
	public bool IsComment => Key is null;

	public static SopsDotEnvLine Entry(string key, string value) => new(key, value);

	public static SopsDotEnvLine Comment(string text) => new(null, text);
}

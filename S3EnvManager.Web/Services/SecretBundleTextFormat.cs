using System.Text;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// 값에 포함된 백슬래시/개행은 이스케이프해 여러 줄 값(PEM/SSH 키 등)도 한 줄로 안전하게 표현한다.
// 편집과 복사가 서로 다른 포맷을 쓰면 붙여넣기 왕복 시 값이 달라지므로 반드시 이 한 곳만 써야 한다.
public static class SecretBundleTextFormat
{
	public static string Format(IEnumerable<KeyValuePair<string, string>> values)
	{
		var builder = new StringBuilder();
		foreach (var (key, value) in values)
		{
			builder.Append(key).Append('=').Append(Escape(value)).Append('\n');
		}
		return builder.ToString();
	}

	public static bool TryParse(string text, out List<KeyValuePair<string, string>> values, out string? error)
	{
		values = [];
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);

		var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
		foreach (var rawLine in normalized.Split('\n'))
		{
			if (rawLine.Length == 0)
			{
				continue;
			}

			var separatorIndex = rawLine.IndexOf('=');
			if (separatorIndex < 0)
			{
				error = $"'=' 기호가 없는 줄입니다: {rawLine}";
				return false;
			}

			var key = rawLine[..separatorIndex];
			var keyError = SecretKeyNameValidator.Validate(key);
			if (keyError is not null)
			{
				error = keyError;
				return false;
			}
			if (!seenKeys.Add(key))
			{
				error = $"키 '{key}'가 중복되었습니다.";
				return false;
			}

			if (!TryUnescape(rawLine[(separatorIndex + 1)..], out var value))
			{
				error = $"키 '{key}'의 값에 잘못된 이스케이프 시퀀스가 있습니다(\\ 뒤에는 \\, n, r만 올 수 있습니다).";
				return false;
			}
			values.Add(new KeyValuePair<string, string>(key, value));
		}

		error = null;
		return true;
	}

	private static string Escape(string value)
	{
		var builder = new StringBuilder(value.Length);
		foreach (var c in value)
		{
			switch (c)
			{
				case '\\': builder.Append("\\\\"); break;
				case '\n': builder.Append("\\n"); break;
				case '\r': builder.Append("\\r"); break;
				default: builder.Append(c); break;
			}
		}
		return builder.ToString();
	}

	private static bool TryUnescape(string raw, out string value)
	{
		var builder = new StringBuilder(raw.Length);
		for (var i = 0; i < raw.Length; i++)
		{
			var c = raw[i];
			if (c != '\\')
			{
				builder.Append(c);
				continue;
			}

			i++;
			if (i >= raw.Length)
			{
				value = string.Empty;
				return false;
			}

			switch (raw[i])
			{
				case 'n': builder.Append('\n'); break;
				case 'r': builder.Append('\r'); break;
				case '\\': builder.Append('\\'); break;
				default:
					value = string.Empty;
					return false;
			}
		}

		value = builder.ToString();
		return true;
	}
}
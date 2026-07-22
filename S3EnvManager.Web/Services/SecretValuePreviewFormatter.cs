using System.Text;

namespace S3EnvManager.Web.Services;

// 판정 결과는 저장하지 않는다 - 표시 시점에만 계산하는 파생값이다.
public static class SecretValuePreviewFormatter
{
	private const Int32 InlineLengthThreshold = 80;
	private const Int32 SummaryFirstLineLength = 60;

	public static bool IsLongValue(string value) =>
		value.Contains('\n') || value.Length > InlineLengthThreshold;

	public static string Summarize(string value)
	{
		var firstLineEnd = value.IndexOf('\n');
		var firstLine = firstLineEnd < 0 ? value : value[..firstLineEnd];
		if (firstLine.Length > SummaryFirstLineLength)
		{
			firstLine = firstLine[..SummaryFirstLineLength] + "…";
		}

		var lineCount = value.Count(c => c == '\n') + 1;
		var byteCount = Encoding.UTF8.GetByteCount(value);
		return lineCount > 1
			? $"{firstLine} … ({lineCount}줄, {FormatSize(byteCount)})"
			: $"{firstLine} ({FormatSize(byteCount)})";
	}

	private static string FormatSize(Int32 bytes) =>
		bytes < 1024 ? $"{bytes}B" : $"{bytes / 1024.0:0.#}KB";
}
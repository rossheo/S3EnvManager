namespace S3EnvManager.Web.Services;

public static class DiscordWebhookUrlValidator
{
	private static readonly string[] AllowedPrefixes =
	[
		"https://discord.com/api/webhooks/",
		"https://discordapp.com/api/webhooks/",
	];

	/// <summary>유효하면 null, 아니면 사용자에게 보여줄 오류 메시지.</summary>
	public static string? Validate(string? url)
	{
		if (string.IsNullOrWhiteSpace(url))
		{
			return "웹훅 URL을 입력하세요.";
		}
		if (!AllowedPrefixes.Any(prefix => url.StartsWith(prefix, StringComparison.Ordinal)))
		{
			return "Discord 웹훅 URL 형식이 올바르지 않습니다(https://discord.com/api/webhooks/... 형태여야 합니다).";
		}
		return null;
	}
}

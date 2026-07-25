using System.Net.Http.Json;

namespace S3EnvManager.Web.Services;

public sealed class DiscordNotifier(HttpClient httpClient, ILogger<DiscordNotifier> logger) : IDiscordNotifier
{
	public async Task SendAsync(
		string webhookUrl, string content, CancellationToken cancellationToken = default)
	{
		try
		{
			var response = await httpClient.PostAsJsonAsync(webhookUrl, new { content }, cancellationToken)
				.ConfigureAwait(false);
			if (!response.IsSuccessStatusCode)
			{
				logger.LogWarning(
					"Discord 웹훅 발송 실패: {StatusCode}", response.StatusCode);
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Discord 웹훅 발송 중 예외가 발생했습니다.");
		}
	}
}

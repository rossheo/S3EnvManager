namespace S3EnvManager.Web.Services;

public interface IDiscordNotifier
{
	// 실패(4xx/5xx, 타임아웃)해도 예외를 던지지 않는다 - 웹훅 하나가 깨졌다고 다른 사용자
	// 알림까지 막으면 안 된다. 실패는 로그로만 남긴다.
	Task SendAsync(string webhookUrl, string content, CancellationToken cancellationToken = default);
}

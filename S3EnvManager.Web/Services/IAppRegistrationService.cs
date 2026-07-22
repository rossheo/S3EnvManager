using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public interface IAppRegistrationService
{
	// 이름이 이미 쓰이고 있거나 버킷에 접근할 수 없으면 등록 전체를 되돌리고 예외를 던진다.
	Task<App> RegisterAsync(string name, CancellationToken cancellationToken = default);

	Task<List<App>> ListActiveAsync(CancellationToken cancellationToken = default);
}

public sealed class AppNameAlreadyExistsException(string name)
	: Exception($"App 이름 '{name}'은(는) 이미 사용 중입니다.");

public sealed class InvalidAppNameException(string reason) : Exception(reason);
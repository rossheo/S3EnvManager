namespace S3EnvManager.Web.Services;

public interface IAppDeletionService
{
	// 자격증명은 즉시 폐기하고 App은 소프트 삭제한다. S3 오브젝트는 60일 뒤 AppPurgeService가 지운다.
	Task DeleteAsync(Guid appId, string? actorUserId = null, CancellationToken cancellationToken = default);
}
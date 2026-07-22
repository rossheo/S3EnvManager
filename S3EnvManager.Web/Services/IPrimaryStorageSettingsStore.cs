namespace S3EnvManager.Web.Services;

public interface IPrimaryStorageSettingsStore
{
	Task SaveAsync(string? region, string? bucket, CancellationToken cancellationToken = default);

	Task<StorageEndpointSettings?> GetAsync(CancellationToken cancellationToken = default);

	// 모든 App이 공유하는 주 저장소 버킷 이름 - 아직 프로비저닝하지 않았으면 null.
	Task<string?> GetLastProvisionedBucketAsync(CancellationToken cancellationToken = default);
}

// 자격증명은 항상 admin 부트스트랩 자격증명을 쓰므로 리전만 갖는다.
public sealed record StorageEndpointSettings(string? Region);

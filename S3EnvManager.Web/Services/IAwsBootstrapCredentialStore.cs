using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// DataProtection(로컬 대칭키)으로 암호화한다 - KMS로 감싸면 순환 참조가 생기기 때문.
public interface IAwsBootstrapCredentialStore
{
	Task SaveAsync(
		CmkRole role, string accessKeyId, string secretAccessKey, CancellationToken cancellationToken = default);

	// "저장된 적 없음"과 "저장은 됐는데 못 읽음"을 반드시 구분해서 돌려준다 - 둘을 null 하나로
	// 뭉개면 호출자가 파괴적으로 오동작한다(AwsAutoProvisioningService가 Access Key를 새로
	// 발급해 AWS의 2개 슬롯을 태우고, 기동 경로는 ambient 자격증명 체인으로 조용히 폴백한다).
	Task<BootstrapCredentialLookup> GetAsync(CmkRole role, CancellationToken cancellationToken = default);

	Task ClearAsync(CmkRole role, CancellationToken cancellationToken = default);
}

public enum BootstrapCredentialStatus
{
	/// <summary>저장된 행이 없다 - 아직 설정하지 않은 정상 상태.</summary>
	NotConfigured,

	/// <summary>행은 있는데 복호화할 수 없다(DataProtection 키링 유실/폐기 의심).
	/// 다시 등록하기 전까지 이 role은 쓸 수 없고, "미설정"처럼 취급하면 안 된다.</summary>
	Unreadable,

	Available,
}

public sealed record BootstrapCredentialLookup(
	BootstrapCredentialStatus Status, string? AccessKeyId, string? SecretAccessKey)
{
	public static readonly BootstrapCredentialLookup NotConfigured =
		new(BootstrapCredentialStatus.NotConfigured, null, null);

	public static readonly BootstrapCredentialLookup Unreadable =
		new(BootstrapCredentialStatus.Unreadable, null, null);

	public static BootstrapCredentialLookup Available(string accessKeyId, string secretAccessKey) =>
		new(BootstrapCredentialStatus.Available, accessKeyId, secretAccessKey);
}
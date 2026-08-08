using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// admin CMK 제거는 S3 버전이 불변이라 noncurrent 버전의 admin 엔트리를 제자리 재래핑할 수 없어
// 그 버전 자체를 영구히 삭제한다(파괴적, 되돌릴 수 없음 - 호출자가 확인받아야 한다).
public interface ICmkRegistryService
{
	Task<List<CmkRegistration>> ListAsync(CancellationToken cancellationToken = default);

	// 등록 즉시 "s3envmanager-managed" 태그를 붙인다 - 태깅 실패 시 등록 전체를 되돌려 실제로
	// 쓸 수 없는 CMK를 레지스트리에 남기지 않는다.
	Task<CmkRegistration> RegisterAsync(
		CmkRole role, string arn, string? actorUserId = null, CancellationToken cancellationToken = default);

	// app role 승격 시 이미 발급된 모든 App 자격증명의 IAM 정책을 등록된 app CMK 전부로 자동
	// 갱신한다 - 자격증명 재발급이 더 이상 필요 없다.
	Task PromoteAsync(Guid cmkId, string? actorUserId = null, CancellationToken cancellationToken = default);

	// admin role 제거는 현재 버전만 재래핑하고, 재래핑 불가능한 noncurrent 버전과 데이터 키
	// 세대는 함께 정리한다(삭제는 파괴적 - 그 버전으로의 롤백이 다시는 불가능해진다).
	Task RemoveAsync(Guid cmkId, string? actorUserId = null, CancellationToken cancellationToken = default);

	// 제거를 누르기 전에 KMS 호출이 몇 번 나갈지 보여주기 위한 상한 견적. free tier(월 2만
	// 요청)를 클릭 한 번으로 태울 수 있는 유일한 경로라 사전에 보여야 한다.
	Task<CmkRemovalEstimate> EstimateRemovalCostAsync(
		Guid cmkId, CancellationToken cancellationToken = default);
}

/// <summary>CMK 제거의 KMS 호출 상한. 실제로는 이 CMK로 감싸지 않은 번들은 건너뛰므로 더 적게
/// 나가지만, 정확히 세려면 번들을 전부 읽어야 해서(그 자체가 S3 GET 폭탄) DB만으로 셀 수 있는
/// 상한을 준다.</summary>
/// <param name="BundleCount">재래핑 대상이 될 수 있는 번들 수(App × Env × base/overwrite).</param>
/// <param name="DataKeyGenerationCount">이 CMK로 감싼 데이터 키 세대 수(app role이면 0).</param>
public sealed record CmkRemovalEstimate(Int32 BundleCount, Int32 DataKeyGenerationCount)
{
	// 재래핑 한 건은 Decrypt 1 + Encrypt 1이다.
	public Int32 MaxKmsCalls => (BundleCount + DataKeyGenerationCount) * 2;
}
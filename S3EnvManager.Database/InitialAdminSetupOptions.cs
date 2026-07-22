namespace S3EnvManager.Database;

/// <summary>"첫 관리자" 부트스트랩용 설정 토큰 - 미설정 시에도 시스템이 자동 생성한 토큰으로
/// 부트스트랩이 가능하다(<see cref="InitialAdminSetupTokenService.MatchesEitherToken"/>).</summary>
public sealed class InitialAdminSetupOptions
{
	public string? Token { get; set; }
}
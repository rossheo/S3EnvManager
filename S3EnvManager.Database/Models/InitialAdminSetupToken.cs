namespace S3EnvManager.Database.Models;

/// <summary>이 토큰(또는 <see cref="InitialAdminSetupOptions.Token"/>)을 아는 사람만 회원가입
/// 시 초기 관리자로 승격될 수 있다 - "최초 가입자가 곧 관리자"인 경쟁(race)을 막는다.</summary>
public class InitialAdminSetupToken
{
	public static readonly Guid SingletonId = Guid.Parse("00000000-0000-0000-0000-0000000000d2");

	public Guid Id { get; init; } = SingletonId;

	public required string Token { get; set; }

	public DateTimeOffset CreatedAt { get; set; }
}
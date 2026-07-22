using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Database;

/// <summary>"첫 관리자" 부트스트랩 - 자동 생성 토큰과 <see cref="InitialAdminSetupOptions.Token"/>
/// 중 하나만 맞아도 회원가입 시 초기 관리자로 승격된다.</summary>
public static class InitialAdminSetupTokenService
{
	private const string TokenAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
	private const Int32 TokenLength = 24;

	public static async Task<bool> IsAdminBootstrapPendingAsync(
		Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
		CancellationToken cancellationToken = default) =>
		(await userManager.GetUsersInRoleAsync(IdentityRoleNames.Administrator)).Count == 0;

	// 여러 인스턴스가 동시에 최초 생성을 시도하는 경쟁은 고정 singleton ID의 유니크 제약으로
	// 막고, 진 쪽은 이긴 쪽이 저장한 값을 그대로 읽어 돌려준다.
	public static async Task<string> EnsureGeneratedTokenAsync(
		ApplicationDbContext db, CancellationToken cancellationToken = default)
	{
		var existing = await db.InitialAdminSetupTokens.AsNoTracking()
			.SingleOrDefaultAsync(t => t.Id == InitialAdminSetupToken.SingletonId, cancellationToken)
			.ConfigureAwait(false);
		if (existing is not null)
		{
			return existing.Token;
		}

		var token = GenerateToken();
		db.InitialAdminSetupTokens.Add(
			new InitialAdminSetupToken { Token = token, CreatedAt = DateTimeOffset.UtcNow });
		try
		{
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
			return token;
		}
		catch (DbUpdateException)
		{
			db.ChangeTracker.Clear();
			var raceWinner = await db.InitialAdminSetupTokens.AsNoTracking()
				.SingleAsync(t => t.Id == InitialAdminSetupToken.SingletonId, cancellationToken).ConfigureAwait(false);
			return raceWinner.Token;
		}
	}

	public static bool MatchesEitherToken(
		string? suppliedToken, string generatedToken, InitialAdminSetupOptions options)
	{
		if (string.IsNullOrWhiteSpace(suppliedToken))
		{
			return false;
		}
		if (string.Equals(suppliedToken, generatedToken, StringComparison.Ordinal))
		{
			return true;
		}
		return !string.IsNullOrWhiteSpace(options.Token) &&
			string.Equals(suppliedToken, options.Token, StringComparison.Ordinal);
	}

	public static async Task LogIfBootstrapPendingAsync(
		ApplicationDbContext db, Microsoft.AspNetCore.Identity.UserManager<ApplicationUser> userManager,
		InitialAdminSetupOptions options, ILogger logger, CancellationToken cancellationToken = default)
	{
		if (!await IsAdminBootstrapPendingAsync(userManager, cancellationToken).ConfigureAwait(false))
		{
			return;
		}

		var generatedToken = await EnsureGeneratedTokenAsync(db, cancellationToken).ConfigureAwait(false);
		logger.LogWarning(
			"관리자 계정이 아직 없습니다 - 회원가입 화면에서 아래 초기 설정 토큰을 입력한 첫 사용자가 " +
			"Administrator 권한을 받습니다: {Token}", generatedToken);
		if (!string.IsNullOrWhiteSpace(options.Token))
		{
			logger.LogWarning("InitialAdminSetup:Token으로 별도 설정된 토큰도 유효합니다.");
		}
	}

	// 사람이 콘솔에 출력된 값을 직접 옮겨 적으므로, 혼동되는 문자(0/O, 1/l/I 등)를 뺀 문자셋을 쓴다.
	private static string GenerateToken()
	{
		Span<byte> bytes = stackalloc byte[TokenLength];
		RandomNumberGenerator.Fill(bytes);
		var chars = new char[TokenLength];
		for (var i = 0; i < bytes.Length; i++)
		{
			chars[i] = TokenAlphabet[bytes[i] % TokenAlphabet.Length];
		}
		return new string(chars);
	}
}
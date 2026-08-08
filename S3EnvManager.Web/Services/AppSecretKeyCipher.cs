using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class AppSecretKeyCipher(ApplicationDbContext db, IKmsKeyOperations kms, IDataKeyCache cache)
	: IAppSecretKeyCipher
{
	private const Int32 NonceSize = 12;
	private const Int32 TagSize = 16;

	// DataKeyGeneration 계열(이 클래스, DataKeyRotationService, CmkRegistryService의 재래핑)은
	// KMS encryption context 없이 감싼다. sops 번들 경로가 {app: appName}을 넣는 것과 다른데,
	// 이유는 감싸는 CMK가 다르기 때문이다: 번들은 Application이 직접 Decrypt하는 app-facing
	// CMK로도 감싸므로 "어느 App의 것인지"를 ciphertext에 묶고 키 정책 조건으로도 쓸 수 있어야
	// 하지만, DataKeyGeneration은 S3EnvManager만 접근 가능한 admin CMK로만 감싸므로 혼동시킬
	// 외부 주체 자체가 없다.
	//
	// 바꾸려면 마이그레이션이 필요하다 - KMS는 wrap 시점과 다른 context면 Decrypt를 거부하므로,
	// 지금 encrypt 쪽에만 context를 넣으면 기존 세대가 전부 열리지 않게 되고 그 세대로 암호화된
	// App 시크릿 키·DbBackupAccount 비밀번호·SharedSecret 값이 통째로 복구 불능이 된다.
	// 세대별 context(또는 버전) 컬럼을 추가하고 재래핑 경로까지 함께 고쳐야 한다.
	private static readonly IReadOnlyDictionary<string, string> NoContext = new Dictionary<string, string>();

	public async Task<(byte[] Ciphertext, Guid DataKeyId)> EncryptAsync(
		string secretKey, CancellationToken cancellationToken = default)
	{
		var (dataKeyId, plaintextKey) = await GetOrCreateCurrentGenerationAsync(cancellationToken)
			.ConfigureAwait(false);

		var nonce = RandomNumberGenerator.GetBytes(NonceSize);
		var plainBytes = Encoding.UTF8.GetBytes(secretKey);
		var cipherBytes = new byte[plainBytes.Length];
		var tag = new byte[TagSize];

		using (var aes = new AesGcm(plaintextKey, TagSize))
		{
			aes.Encrypt(nonce, plainBytes, cipherBytes, tag);
		}

		var blob = new byte[NonceSize + TagSize + cipherBytes.Length];
		Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
		Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
		Buffer.BlockCopy(cipherBytes, 0, blob, NonceSize + TagSize, cipherBytes.Length);
		return (blob, dataKeyId);
	}

	public async Task<string> DecryptAsync(
		byte[] ciphertext, Guid dataKeyId, CancellationToken cancellationToken = default)
	{
		var plaintextKey = await GetGenerationPlaintextAsync(dataKeyId, cancellationToken).ConfigureAwait(false);

		var nonce = ciphertext[..NonceSize];
		var tag = ciphertext[NonceSize..(NonceSize + TagSize)];
		var cipherBytes = ciphertext[(NonceSize + TagSize)..];
		var plainBytes = new byte[cipherBytes.Length];

		using var aes = new AesGcm(plaintextKey, TagSize);
		aes.Decrypt(nonce, cipherBytes, tag, plainBytes);
		return Encoding.UTF8.GetString(plainBytes);
	}

	private async Task<(Guid DataKeyId, byte[] Plaintext)> GetOrCreateCurrentGenerationAsync(
		CancellationToken cancellationToken)
	{
		var latest = await db.DataKeyGenerations.AsNoTracking()
			.OrderByDescending(d => d.CreatedAt)
			.FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
		if (latest is not null)
		{
			return (latest.KeyId, await GetGenerationPlaintextAsync(latest, cancellationToken).ConfigureAwait(false));
		}

		var adminCmk = await db.CmkRegistrations.AsNoTracking()
			.SingleOrDefaultAsync(c => c.Role == CmkRole.Admin && c.Status == CmkStatus.Active, cancellationToken)
			.ConfigureAwait(false)
			?? throw new InvalidOperationException("admin role의 활성 CMK가 등록되어 있지 않습니다.");

		var (plaintextKey, ciphertextBlob) = await kms.GenerateDataKeyAsync(
			adminCmk.Arn, NoContext, cancellationToken)
			.ConfigureAwait(false);

		var generation = new DataKeyGeneration
		{
			KeyId = Guid.NewGuid(),
			CiphertextBlob = ciphertextBlob,
			CmkId = adminCmk.CmkId,
			CreatedAt = DateTimeOffset.UtcNow,
		};
		db.DataKeyGenerations.Add(generation);
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		cache.Set(generation.KeyId, plaintextKey);
		return (generation.KeyId, plaintextKey);
	}

	private async Task<byte[]> GetGenerationPlaintextAsync(Guid dataKeyId, CancellationToken cancellationToken)
	{
		if (cache.TryGet(dataKeyId, out var cached))
		{
			return cached;
		}

		var generation = await db.DataKeyGenerations.AsNoTracking()
			.SingleAsync(d => d.KeyId == dataKeyId, cancellationToken).ConfigureAwait(false);
		return await GetGenerationPlaintextAsync(generation, cancellationToken).ConfigureAwait(false);
	}

	private async Task<byte[]> GetGenerationPlaintextAsync(
		DataKeyGeneration generation, CancellationToken cancellationToken)
	{
		if (cache.TryGet(generation.KeyId, out var cached))
		{
			return cached;
		}

		var cmkArn = await db.CmkRegistrations.AsNoTracking()
			.Where(c => c.CmkId == generation.CmkId)
			.Select(c => c.Arn)
			.SingleAsync(cancellationToken).ConfigureAwait(false);

		var plaintextKey = await kms.DecryptAsync(cmkArn, generation.CiphertextBlob, NoContext, cancellationToken)
			.ConfigureAwait(false);
		cache.Set(generation.KeyId, plaintextKey);
		return plaintextKey;
	}
}
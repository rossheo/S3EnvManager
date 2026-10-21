namespace S3EnvManager.Web.Services;

/// <summary>SecretKey를 KMS envelope encryption 데이터 키로 암/복호화한다.</summary>
public interface IAppSecretKeyCipher
{
	Task<(byte[] Ciphertext, Guid DataKeyId)> EncryptAsync(
		string secretKey, CancellationToken cancellationToken = default);

	Task<string> DecryptAsync(
		byte[] ciphertext, Guid dataKeyId, CancellationToken cancellationToken = default);
}
namespace S3EnvManager.Sops;

/// <summary>sops 데이터 키를 감싸는 KMS 호출 추상화(실서비스: <see cref="AwsKmsKeyOperations"/>).</summary>
public interface IKmsKeyOperations
{
	/// <summary>새 평문 데이터 키(32바이트)를 생성하고 지정 CMK로 감싼 ciphertext blob도 반환한다.</summary>
	Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
		string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default);

	/// <summary>기존 평문 데이터 키를 다른 CMK로 감싼다(다중 wrap용 두 번째 ciphertext blob 생성).</summary>
	Task<byte[]> EncryptAsync(
		string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default);

	/// <summary>ciphertext blob을 평문 데이터 키로 복호화한다.</summary>
	Task<byte[]> DecryptAsync(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default);
}
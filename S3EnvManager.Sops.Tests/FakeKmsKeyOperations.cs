using S3EnvManager.Sops;

namespace S3EnvManager.Sops.Tests;

/// <summary>KMS 없이 envelope wrap/unwrap을 검증하기 위한 인메모리 가짜 구현.</summary>
public sealed class FakeKmsKeyOperations : IKmsKeyOperations
{
	private readonly Dictionary<Guid,
		(string CmkArn, byte[] Plaintext, IReadOnlyDictionary<string, string> Context)> _store = [];

	public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
		string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var plaintext = new byte[32];
		System.Security.Cryptography.RandomNumberGenerator.Fill(plaintext);
		var id = Guid.NewGuid();
		_store[id] = (cmkArn, plaintext, new Dictionary<string, string>(encryptionContext));
		return Task.FromResult((plaintext, id.ToByteArray()));
	}

	public Task<byte[]> EncryptAsync(
		string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var id = Guid.NewGuid();
		_store[id] = (cmkArn, plaintextKey, new Dictionary<string, string>(encryptionContext));
		return Task.FromResult(id.ToByteArray());
	}

	public Task<byte[]> DecryptAsync(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var id = new Guid(ciphertextBlob);
		if (!_store.TryGetValue(id, out var record))
		{
			throw new InvalidOperationException("알 수 없는 ciphertext blob입니다.");
		}
		if (record.CmkArn != cmkArn)
		{
			throw new InvalidOperationException("이 CMK로는 복호화할 권한이 없습니다(가짜 AccessDenied).");
		}
		if (record.Context.Count != encryptionContext.Count ||
			record.Context.Any(kv => !encryptionContext.TryGetValue(kv.Key, out var v) || v != kv.Value))
		{
			throw new InvalidOperationException("encryption context가 일치하지 않습니다(가짜 InvalidCiphertext).");
		}
		return Task.FromResult(record.Plaintext);
	}
}
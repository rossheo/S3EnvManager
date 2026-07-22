using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;

namespace S3EnvManager.Sops;

/// <summary>AWSSDK.KeyManagementService를 통해 실제(또는 LocalStack) KMS를 호출하는 구현.</summary>
public sealed class AwsKmsKeyOperations(IAmazonKeyManagementService client) : IKmsKeyOperations
{
	public async Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
		string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var response = await client.GenerateDataKeyAsync(new GenerateDataKeyRequest
		{
			KeyId = cmkArn,
			KeySpec = DataKeySpec.AES_256,
			EncryptionContext = ToMutableDictionary(encryptionContext),
		}, cancellationToken).ConfigureAwait(false);

		return (response.Plaintext.ToArray(), response.CiphertextBlob.ToArray());
	}

	public async Task<byte[]> EncryptAsync(
		string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		using var plaintextStream = new MemoryStream(plaintextKey, writable: false);
		var response = await client.EncryptAsync(new EncryptRequest
		{
			KeyId = cmkArn,
			Plaintext = plaintextStream,
			EncryptionContext = ToMutableDictionary(encryptionContext),
		}, cancellationToken).ConfigureAwait(false);

		return response.CiphertextBlob.ToArray();
	}

	public async Task<byte[]> DecryptAsync(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		using var ciphertextStream = new MemoryStream(ciphertextBlob, writable: false);
		var response = await client.DecryptAsync(new DecryptRequest
		{
			KeyId = cmkArn,
			CiphertextBlob = ciphertextStream,
			EncryptionContext = ToMutableDictionary(encryptionContext),
		}, cancellationToken).ConfigureAwait(false);

		return response.Plaintext.ToArray();
	}

	private static Dictionary<string, string> ToMutableDictionary(IReadOnlyDictionary<string, string> source) =>
		new(source);
}
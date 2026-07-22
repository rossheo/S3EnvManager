using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace S3EnvManager.Sops;

/// <summary>sops의 값 단위 AES-256-GCM 재현(getsops/sops v3.13.2 aes/cipher.go). sops는 32바이트
/// GCM nonce를 쓰므로 .NET 표준 AesGcm(고정 12바이트)으로는 구현 불가 - BouncyCastle
/// GcmBlockCipher를 직접 구성해서 쓴다.</summary>
public static partial class SopsValueCipher
{
	private const Int32 NonceSizeBytes = 32;
	private const Int32 TagSizeBytes = 16;

	[GeneratedRegex(
		@"^ENC\[AES256_GCM,data:(?<data>[^,]*),iv:(?<iv>[^,]*),tag:(?<tag>[^,]*),type:(?<type>[a-z]+)\]$")]
	private static partial Regex EncryptedValuePattern();

	/// <summary>평문 문자열 하나를 sops 포맷의 "ENC[...]" 문자열로 암호화한다.</summary>
	public static string Encrypt(string plaintext, byte[] dataKey, string additionalData)
	{
		if (plaintext.Length == 0)
		{
			return string.Empty;
		}

		var iv = new byte[NonceSizeBytes];
		System.Security.Cryptography.RandomNumberGenerator.Fill(iv);

		var plainBytes = Encoding.UTF8.GetBytes(plaintext);
		var (data, tag) = SealCore(dataKey, iv, plainBytes, additionalData);

		return $"ENC[AES256_GCM,data:{Convert.ToBase64String(data)},iv:{Convert.ToBase64String(iv)}," +
			$"tag:{Convert.ToBase64String(tag)},type:str]";
	}

	/// <summary>sops 포맷의 "ENC[...]" 문자열을 평문 문자열로 복호화한다.</summary>
	public static string Decrypt(string encryptedValue, byte[] dataKey, string additionalData)
	{
		if (encryptedValue.Length == 0)
		{
			return string.Empty;
		}

		var match = EncryptedValuePattern().Match(encryptedValue);
		if (!match.Success)
		{
			throw new FormatException($"sops 값 포맷과 일치하지 않습니다: {encryptedValue}");
		}

		var data = Convert.FromBase64String(match.Groups["data"].Value);
		var iv = Convert.FromBase64String(match.Groups["iv"].Value);
		var tag = Convert.FromBase64String(match.Groups["tag"].Value);

		var plainBytes = OpenCore(dataKey, iv, data, tag, additionalData);
		return Encoding.UTF8.GetString(plainBytes);
	}

	private static (byte[] Data, byte[] Tag) SealCore(
		byte[] key, byte[] iv, byte[] plainBytes, string additionalData)
	{
		var gcm = new GcmBlockCipher(new AesEngine());
		var parameters = new AeadParameters(
			new KeyParameter(key), TagSizeBytes * 8, iv, Encoding.UTF8.GetBytes(additionalData));
		gcm.Init(forEncryption: true, parameters);

		var output = new byte[gcm.GetOutputSize(plainBytes.Length)];
		var length = gcm.ProcessBytes(plainBytes, 0, plainBytes.Length, output, 0);
		length += gcm.DoFinal(output, length);

		var data = new byte[length - TagSizeBytes];
		var tag = new byte[TagSizeBytes];
		Buffer.BlockCopy(output, 0, data, 0, data.Length);
		Buffer.BlockCopy(output, data.Length, tag, 0, TagSizeBytes);
		return (data, tag);
	}

	private static byte[] OpenCore(byte[] key, byte[] iv, byte[] data, byte[] tag, string additionalData)
	{
		var cipherAndTag = new byte[data.Length + tag.Length];
		Buffer.BlockCopy(data, 0, cipherAndTag, 0, data.Length);
		Buffer.BlockCopy(tag, 0, cipherAndTag, data.Length, tag.Length);

		var gcm = new GcmBlockCipher(new AesEngine());
		var parameters = new AeadParameters(
			new KeyParameter(key), TagSizeBytes * 8, iv, Encoding.UTF8.GetBytes(additionalData));
		gcm.Init(forEncryption: false, parameters);

		var output = new byte[gcm.GetOutputSize(cipherAndTag.Length)];
		Int32 length;
		try
		{
			length = gcm.ProcessBytes(cipherAndTag, 0, cipherAndTag.Length, output, 0);
			length += gcm.DoFinal(output, length);
		}
		catch (Org.BouncyCastle.Crypto.InvalidCipherTextException ex)
		{
			throw new CryptographicMacException("sops 값의 GCM 태그 검증에 실패했습니다(변조 또는 잘못된 키).", ex);
		}

		if (length == output.Length)
		{
			return output;
		}

		var trimmed = new byte[length];
		Buffer.BlockCopy(output, 0, trimmed, 0, length);
		return trimmed;
	}
}

/// <summary>sops MAC 또는 GCM 태그 검증 실패를 나타낸다 - 값이 위변조되었거나 키가 잘못됐다는 뜻이다.</summary>
public sealed class CryptographicMacException : Exception
{
	public CryptographicMacException(string message, Exception innerException) : base(message, innerException)
	{
	}
}
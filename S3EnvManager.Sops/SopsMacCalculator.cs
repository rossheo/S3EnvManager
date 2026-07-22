using System.Security.Cryptography;
using System.Text;

namespace S3EnvManager.Sops;

/// <summary>sops의 MAC 계산 재현(sops.go Tree.Encrypt/Decrypt): 값들의 평문 UTF8 바이트를
/// 순서대로 SHA-512에 누적, 대문자 hex 문자열화한 것이 MAC 평문.</summary>
public sealed class SopsMacCalculator
{
	private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

	public void Append(string plaintextValue)
	{
		_hash.AppendData(Encoding.UTF8.GetBytes(plaintextValue));
	}

	/// <summary>지금까지 누적된 값으로 MAC 평문(대문자 hex 문자열)을 계산한다.</summary>
	public string ComputeHex()
	{
		var digest = _hash.GetCurrentHash();
		return Convert.ToHexString(digest);
	}
}
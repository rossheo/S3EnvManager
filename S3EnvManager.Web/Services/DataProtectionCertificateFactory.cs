using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace S3EnvManager.Web.Services;

/// <summary>자체 서명된 RSA 인증서를 발급한다 - CA 서명/신뢰 체인은 필요 없다(TLS가 아니라
/// DataProtection XML 키 wrap 전용 용도).</summary>
public static class DataProtectionCertificateFactory
{
	private const Int32 KeySizeInBits = 2048;

	public static (
		X509Certificate2 Certificate, byte[] Pkcs12, DateTimeOffset NotBefore, DateTimeOffset NotAfter)
		CreateSelfSigned(Int32 validityYears, string password, TimeProvider timeProvider)
	{
		using var rsa = RSA.Create(KeySizeInBits);
		var request = new CertificateRequest(
			"CN=S3EnvManager DataProtection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

		var notBefore = timeProvider.GetUtcNow();
		var notAfter = notBefore.AddYears(validityYears);
		using var created = request.CreateSelfSigned(notBefore, notAfter);

		var pkcs12 = created.Export(X509ContentType.Pkcs12, password);
		// 저장/캐싱에 쓰일 인스턴스를 항상 Load() 경로와 동일하게 맞추기 위해 재로드한다.
		var reloaded = Load(pkcs12, password);
		return (reloaded, pkcs12, notBefore, notAfter);
	}

	public static X509Certificate2 Load(byte[] pkcs12, string password) =>
		X509CertificateLoader.LoadPkcs12(pkcs12, password, X509KeyStorageFlags.EphemeralKeySet);
}
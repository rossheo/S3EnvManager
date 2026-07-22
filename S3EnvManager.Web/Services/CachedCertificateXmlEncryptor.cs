using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Logging.Abstractions;

namespace S3EnvManager.Web.Services;

/// <summary>DecryptorType을 프레임워크 기본값이 아닌 <see cref="CachedCertificateXmlDecryptor"/>로 지정해야
/// 복호화 시에도 동적 캐시를 다시 조회한다 - 기본 EncryptedXmlDecryptor는 부팅 시 고정된 인증서만 본다.</summary>
public sealed class CachedCertificateXmlEncryptor(DataProtectionCertificateCache cache) : IXmlEncryptor
{
	public EncryptedXmlInfo Encrypt(XElement plaintextElement)
	{
		var certificate = cache.GetActive()
			?? throw new InvalidOperationException("사용 가능한 DataProtection 인증서가 없습니다.");
		var inner = new CertificateXmlEncryptor(certificate, NullLoggerFactory.Instance).Encrypt(plaintextElement);
		return new EncryptedXmlInfo(inner.EncryptedElement, typeof(CachedCertificateXmlDecryptor));
	}
}
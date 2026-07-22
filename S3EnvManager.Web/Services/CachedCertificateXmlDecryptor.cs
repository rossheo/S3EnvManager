using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace S3EnvManager.Web.Services;

// DataProtection의 SimpleActivator는 매개변수 없는 생성자 또는 IServiceProvider 단일 매개변수
// 생성자만 지원한다 - DataProtectionCertificateCache를 직접 주입받는 생성자였을 때
// "No parameterless constructor defined"로 실제 크래시가 났다. 그래서 IServiceProvider를 받아
// 내부에서 캐시를 해석한다. 매 호출마다 미니 DI 컨테이너를 새로 만들지만, 키 세대가 바뀔 때만
// 드물게 호출되므로 오버헤드는 무시할 만하다.
public sealed class CachedCertificateXmlDecryptor(IServiceProvider serviceProvider) : IXmlDecryptor
{
	public XElement Decrypt(XElement encryptedElement)
	{
		var cache = serviceProvider.GetRequiredService<DataProtectionCertificateCache>();
		var candidates = cache.GetAll();
		if (candidates.Count == 0)
		{
			throw new InvalidOperationException("등록된 DataProtection 인증서가 없어 복호화할 수 없습니다.");
		}

		var miniServices = new ServiceCollection();
		miniServices.AddDataProtection().UnprotectKeysWithAnyCertificate([.. candidates]);
		using var miniProvider = miniServices.BuildServiceProvider();
		return new EncryptedXmlDecryptor(miniProvider).Decrypt(encryptedElement);
	}
}

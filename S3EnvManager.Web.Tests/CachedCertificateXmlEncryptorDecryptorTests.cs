using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Internal;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>회귀 방지: 반드시 실제 런타임이 쓰는 <see cref="IActivator"/>(SimpleActivator)로
/// 인스턴스를 만들어야 한다. ActivatorUtilities로 대체하면 SimpleActivator가 요구하는
/// 매개변수 없는/IServiceProvider 단일 매개변수 생성자 제약을 놓친다 - 과거 이 차이 때문에
/// k8s에서만 "No parameterless constructor defined"로 크래시가 나고 이 테스트는 계속 통과했다.</summary>
public class CachedCertificateXmlEncryptorDecryptorTests
{
	private static IActivator CreateActivator(DataProtectionCertificateCache cache, out ServiceProvider provider)
	{
		var services = new ServiceCollection();
		services.AddSingleton(cache);
		services.AddDataProtection();
		provider = services.BuildServiceProvider();
		return provider.GetRequiredService<IActivator>();
	}

	[Fact]
	public void Encrypt_ThenDecrypt_ViaDecryptorTypeActivation_RoundTrips()
	{
		var cache = new DataProtectionCertificateCache();
		var (cert, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(1, "pw", TimeProvider.System);
		cache.ReplaceSnapshot([cert]);

		var activator = CreateActivator(cache, out var provider);
		using var _ = provider;

		var encryptor = new CachedCertificateXmlEncryptor(cache);
		var plaintext = new XElement("secret", "hello-world");
		var encrypted = encryptor.Encrypt(plaintext);

		Assert.Equal(typeof(CachedCertificateXmlDecryptor), encrypted.DecryptorType);

		var decryptor = (IXmlDecryptor)activator.CreateInstance(typeof(IXmlDecryptor), encrypted.DecryptorType.AssemblyQualifiedName!);
		var roundTrip = decryptor.Decrypt(encrypted.EncryptedElement);

		Assert.Equal(plaintext.ToString(), roundTrip.ToString());
	}

	[Fact]
	public void Rotation_WithoutRebuildingServiceProvider_OldAndNewGenerationsBothDecrypt()
	{
		var cache = new DataProtectionCertificateCache();
		var (gen1Cert, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(1, "pw1", TimeProvider.System);
		cache.ReplaceSnapshot([gen1Cert]);

		var activator = CreateActivator(cache, out var provider);
		using var _ = provider;
		var encryptor = new CachedCertificateXmlEncryptor(cache);

		var gen1Plain = new XElement("secret", "generation-1");
		var gen1Encrypted = encryptor.Encrypt(gen1Plain);

		var (gen2Cert, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(1, "pw2", TimeProvider.System);
		cache.ReplaceSnapshot([gen2Cert, gen1Cert]);

		var gen2Plain = new XElement("secret", "generation-2");
		var gen2Encrypted = encryptor.Encrypt(gen2Plain);

		var gen1Decryptor = (IXmlDecryptor)activator.CreateInstance(typeof(IXmlDecryptor), gen1Encrypted.DecryptorType.AssemblyQualifiedName!);
		var gen2Decryptor = (IXmlDecryptor)activator.CreateInstance(typeof(IXmlDecryptor), gen2Encrypted.DecryptorType.AssemblyQualifiedName!);

		Assert.Equal(gen1Plain.ToString(), gen1Decryptor.Decrypt(gen1Encrypted.EncryptedElement).ToString());
		Assert.Equal(gen2Plain.ToString(), gen2Decryptor.Decrypt(gen2Encrypted.EncryptedElement).ToString());
	}

	[Fact]
	public void Decrypt_WhenCacheIsEmpty_ThrowsClearly()
	{
		var cache = new DataProtectionCertificateCache();
		var activator = CreateActivator(cache, out var provider);
		using var _ = provider;

		var (cert, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(1, "pw", TimeProvider.System);
		var encryptor = new CachedCertificateXmlEncryptor(CreateCacheWith(cert));
		var encrypted = encryptor.Encrypt(new XElement("secret", "value"));

		var decryptor = (IXmlDecryptor)activator.CreateInstance(typeof(IXmlDecryptor), encrypted.DecryptorType.AssemblyQualifiedName!);
		Assert.Throws<InvalidOperationException>(() => decryptor.Decrypt(encrypted.EncryptedElement));
	}

	private static DataProtectionCertificateCache CreateCacheWith(
		System.Security.Cryptography.X509Certificates.X509Certificate2 cert)
	{
		var cache = new DataProtectionCertificateCache();
		cache.ReplaceSnapshot([cert]);
		return cache;
	}
}

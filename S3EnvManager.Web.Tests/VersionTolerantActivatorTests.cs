using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Internal;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class VersionTolerantActivatorTests
{
	private sealed class RecordingActivator : IActivator
	{
		public string? ReceivedTypeName;

		public object CreateInstance(Type expectedBaseType, string implementationTypeName)
		{
			ReceivedTypeName = implementationTypeName;
			return new object();
		}
	}

	[Fact]
	public void CreateInstance_StripsVersionFromOwnAssemblyTypeName()
	{
		var inner = new RecordingActivator();
		var activator = new VersionTolerantActivator(inner);
		var staleTypeName =
			"S3EnvManager.Web.Services.CachedCertificateXmlDecryptor, S3EnvManager.Web, " +
			"Version=1.2607.1.0, Culture=neutral, PublicKeyToken=null";

		activator.CreateInstance(typeof(object), staleTypeName);

		Assert.Equal(
			"S3EnvManager.Web.Services.CachedCertificateXmlDecryptor, S3EnvManager.Web", inner.ReceivedTypeName);
	}

	[Fact]
	public void CreateInstance_LeavesForeignAssemblyTypeNameUntouched()
	{
		var inner = new RecordingActivator();
		var activator = new VersionTolerantActivator(inner);
		const string frameworkTypeName =
			"Microsoft.AspNetCore.DataProtection.XmlEncryption.EncryptedXmlDecryptor, " +
			"Microsoft.AspNetCore.DataProtection, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60";

		activator.CreateInstance(typeof(object), frameworkTypeName);

		Assert.Equal(frameworkTypeName, inner.ReceivedTypeName);
	}

	/// <summary>실제 배포 크래시를 재현한다: 이전 배포(AssemblyVersion이 다름)가 남긴 것과 같은
	/// 문자열을, 프레임워크의 실제 IActivator 체인(ReplaceDefault로 감싼 것)에 흘려보내
	/// CachedCertificateXmlDecryptor 인스턴스가 실제로 만들어지는지 확인한다.</summary>
	[Fact]
	public void ReplaceDefault_ResolvesStaleVersionedOwnTypeThroughRealActivatorChain()
	{
		var services = new ServiceCollection();
		services.AddDataProtection();
		VersionTolerantActivator.ReplaceDefault(services);
		using var provider = services.BuildServiceProvider();

		var activator = provider.GetRequiredService<IActivator>();
		var staleTypeName =
			"S3EnvManager.Web.Services.CachedCertificateXmlDecryptor, S3EnvManager.Web, " +
			"Version=1.2607.1.0, Culture=neutral, PublicKeyToken=null";

		var instance = activator.CreateInstance(typeof(IXmlDecryptor), staleTypeName);

		Assert.IsType<CachedCertificateXmlDecryptor>(instance);
	}
}

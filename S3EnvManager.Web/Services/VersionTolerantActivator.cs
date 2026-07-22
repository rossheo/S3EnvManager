using Microsoft.AspNetCore.DataProtection.Internal;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace S3EnvManager.Web.Services;

// AssemblyVersion이 커밋마다 바뀌어(Directory.Build.targets GitInformation), 이전 배포가 남긴 키의
// 버전 문자열을 새 배포가 그대로 못 찾아 죽던 문제 방지용.
public sealed class VersionTolerantActivator(IActivator inner) : IActivator
{
	private static readonly string OwnAssemblySimpleName =
		typeof(VersionTolerantActivator).Assembly.GetName().Name!;

	public object CreateInstance(Type expectedBaseType, string implementationTypeName) =>
		inner.CreateInstance(expectedBaseType, StripOwnAssemblyVersion(implementationTypeName));

	private static string StripOwnAssemblyVersion(string typeName)
	{
		var parts = typeName.Split(',');
		if (parts.Length < 2 || parts[1].Trim() != OwnAssemblySimpleName)
		{
			return typeName;
		}
		return $"{parts[0].Trim()}, {OwnAssemblySimpleName}";
	}

	// 기본 IActivator 구현(TypeForwardingActivator)이 internal이라 직접 new할 수 없어 DI 등록을 재사용한다.
	public static void ReplaceDefault(IServiceCollection services)
	{
		var defaultDescriptor = services.Last(sd => sd.ServiceType == typeof(IActivator));
		if (defaultDescriptor.ImplementationType is not { } implementationType)
		{
			throw new InvalidOperationException(
				"기본 IActivator 등록 방식이 예상과 달라 VersionTolerantActivator로 감쌀 수 없습니다.");
		}

		services.Replace(ServiceDescriptor.Singleton<IActivator>(sp =>
		{
			var defaultActivator = (IActivator)ActivatorUtilities.CreateInstance(sp, implementationType);
			return new VersionTolerantActivator(defaultActivator);
		}));
	}
}

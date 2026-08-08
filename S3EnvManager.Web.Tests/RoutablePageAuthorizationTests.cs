using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using S3EnvManager.Web.Components.Pages;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>라우팅 가능한 모든 페이지가 인가 데이터를 갖는지 컴파일 결과에서 직접 확인한다.
/// Components/Pages/_Imports.razor의 @attribute [Authorize]가 하위 폴더까지 실제로 붙는지를
/// 검증하는 것이 목적이다 - 새 페이지에서 속성을 빼먹어도 익명 접근이 되지 않아야 한다.
///
/// Blazor의 AuthorizeRouteView가 쓰는 판정과 같은 규칙을 재현한다: 타입에 붙은 특성 중
/// IAllowAnonymous가 하나라도 있으면 인가 데이터를 통째로 비우고, 아니면 IAuthorizeData를
/// 모은다.</summary>
public class RoutablePageAuthorizationTests
{
	// 익명 접근이 의도된 페이지. 여기에 추가하려면 그 이유가 페이지 주석에 있어야 한다.
	private static readonly HashSet<string> IntentionallyAnonymous =
	[
		nameof(Dashboard),
		nameof(Error),
		nameof(NotFound),
	];

	public static TheoryData<Type> RoutablePageTypes()
	{
		var data = new TheoryData<Type>();
		foreach (var type in typeof(Dashboard).Assembly.GetTypes())
		{
			var isRoutableComponent =
				typeof(IComponent).IsAssignableFrom(type) &&
				type.GetCustomAttributes(typeof(RouteAttribute), inherit: true).Length > 0 &&
				type.Namespace?.StartsWith("S3EnvManager.Web.Components.Pages", StringComparison.Ordinal) == true;
			if (isRoutableComponent)
			{
				data.Add(type);
			}
		}
		return data;
	}

	[Theory]
	[MemberData(nameof(RoutablePageTypes))]
	public void EveryRoutablePage_RequiresAuthorization_UnlessExplicitlyAnonymous(Type pageType)
	{
		var attributes = pageType.GetCustomAttributes(inherit: true);
		var allowsAnonymous = attributes.Any(a => a is IAllowAnonymous);
		var authorizeData = attributes.OfType<IAuthorizeData>().ToList();

		if (IntentionallyAnonymous.Contains(pageType.Name))
		{
			Assert.True(allowsAnonymous,
				$"{pageType.Name}은(는) 익명 허용 대상인데 [AllowAnonymous]가 없습니다.");
			return;
		}

		Assert.False(allowsAnonymous, $"{pageType.Name}에 예상치 못한 [AllowAnonymous]가 붙어 있습니다.");
		Assert.NotEmpty(authorizeData);
	}

	// _Imports.razor가 하위 폴더(Admin/, Apps/, Settings/)까지 적용되는지 - 페이지가 자체
	// [Authorize]를 갖고 있으면 그것만으로도 위 테스트는 통과하므로, 상속분이 실제로 붙었는지
	// 별도로 확인한다.
	[Fact]
	public void ImportsAuthorizeAttribute_AppliesToNestedFolders()
	{
		// Roles 없는 [Authorize]가 _Imports에서 온 것이다(모든 페이지는 자체적으로는
		// Roles를 지정한다).
		var inherited = typeof(S3EnvManager.Web.Components.Pages.Admin.Users)
			.GetCustomAttributes(inherit: true)
			.OfType<IAuthorizeData>()
			.Where(a => string.IsNullOrEmpty(a.Roles) && string.IsNullOrEmpty(a.Policy));
		Assert.NotEmpty(inherited);
	}

	// 라우팅 가능한 페이지를 하나도 못 찾으면 위 Theory가 0건으로 조용히 통과한다.
	[Fact]
	public void RoutablePageTypes_AreDiscovered()
	{
		Assert.True(RoutablePageTypes().Count >= 15, "라우팅 가능한 페이지 탐색이 실패했습니다.");
	}
}

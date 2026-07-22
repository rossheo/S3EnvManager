using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>회귀 방지: 설계 시점 팩토리가 Program.cs와 다른 모델을 만들어 AspNetUserPasskeys
/// 마이그레이션이 누락되고 런타임에 "relation does not exist"로 크래시한 적이 있다.</summary>
public class ApplicationDbContextDesignTimeFactoryTests
{
	[Fact]
	public void CreateDbContext_TableSet_MatchesDiConfiguredRuntimeModel()
	{
		var designTimeContext = new ApplicationDbContextDesignTimeFactory().CreateDbContext([]);
		var designTimeTables = designTimeContext.Model.GetEntityTypes()
			.Select(e => e.GetTableName())
			.OrderBy(x => x, StringComparer.Ordinal)
			.ToList();

		var runtimeTables = CreateRuntimeConfiguredContext().Model.GetEntityTypes()
			.Select(e => e.GetTableName())
			.OrderBy(x => x, StringComparer.Ordinal)
			.ToList();

		Assert.Equal(runtimeTables, designTimeTables);
	}

	/// <summary>Program.cs의 Identity DI 배선을 그대로 재현한다(모델 빌드만, 실제 연결 불필요).</summary>
	private static ApplicationDbContext CreateRuntimeConfiguredContext()
	{
		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(options =>
			options.UseNpgsql(
				"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres"));
		services.AddIdentityCore<ApplicationUser>(options =>
			{
				options.Stores.SchemaVersion = ApplicationIdentitySchema.Version;
			})
			.AddRoles<IdentityRole>()
			.AddEntityFrameworkStores<ApplicationDbContext>();

		var provider = services.BuildServiceProvider();
		return provider.GetRequiredService<ApplicationDbContext>();
	}
}
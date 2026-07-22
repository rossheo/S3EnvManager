using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Database;

/// <summary>`dotnet ef migrations add`용 설계 시점 팩토리 - AppHost 없이 로컬 Postgres에
/// 연결한다. Identity 스키마 반영을 위해 DI를 거쳐 컨텍스트를 구성해야 하며(직접 생성 시
/// Version3 테이블인 AspNetUserPasskeys 등이 마이그레이션에서 누락됨), Program.cs와 반드시
/// 같은 <see cref="ApplicationIdentitySchema.Version"/>을 써야 한다.</summary>
public class ApplicationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ApplicationDbContext>
{
	public ApplicationDbContext CreateDbContext(string[] args)
	{
		var connectionString = Environment.GetEnvironmentVariable("S3ENVMANAGER_MIGRATIONS_CONNECTION_STRING")
			?? "Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString));
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
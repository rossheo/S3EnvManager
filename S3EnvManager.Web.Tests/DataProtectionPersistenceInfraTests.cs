using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using S3EnvManager.Database;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

/// <summary>DI 등록이 컴파일된다는 것과 실제로 키가 테이블에 쓰이는 것은 별개다 -
/// Protect()가 키링 초기화를 트리거하는 지연 동작이라 직접 호출해봐야 확인된다.</summary>
public class DataProtectionPersistenceInfraTests
{
	private const string PostgresConnectionString =
		"Host=localhost;Port=55432;Database=s3envmanagerdb;Username=postgres;Password=postgres";

	[Fact]
	public async Task Protect_PersistsKeyToPostgresDataProtectionKeysTable()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(PostgresConnectionString));
		services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();
		await using var provider = services.BuildServiceProvider();

		var protector = provider.GetRequiredService<IDataProtectionProvider>()
			.CreateProtector("DataProtectionPersistenceInfraTests");
		var protectedValue = protector.Protect("hello");
		var unprotectedValue = protector.Unprotect(protectedValue);

		Assert.Equal("hello", unprotectedValue);

		await using var verifyDb = new ApplicationDbContext(
			new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
		var keys = await verifyDb.DataProtectionKeys.ToListAsync();
		Assert.NotEmpty(keys);
	}

	/// <summary>마스터 키가 평문이 아니라 encryptedSecret으로 감싸져 저장되는지 확인한다.
	///
	/// 여기서 키를 명시적으로 새로 만드는 이유: DataProtection은 유효한 키가 이미 있으면 새 키를
	/// 만들지 않는다. DataProtectionKeys는 모든 테스트가 공유하는 테이블이라, 앞선 테스트가
	/// 암호화기 없이 만든 키가 남아 있으면 Protect()가 그 키를 그대로 재사용해 새 행이 생기지
	/// 않는다 - "가장 최근 행"을 보는 방식으로는 이 단언이 통과할 수 없다(실제로 그래서 실패하고
	/// 있었다). 이번 호출이 만든 키를 KeyId로 콕 집어 확인한다.</summary>
	[Fact]
	public async Task Protect_WithCertificateXmlEncryptor_PersistsEncryptedKey_NotPlaintext()
	{
		if (!await IsPostgresAvailableAsync())
		{
			return;
		}

		var cache = new DataProtectionCertificateCache();
		var (certificate, _, _, _) = DataProtectionCertificateFactory.CreateSelfSigned(
			1, "pw", TimeProvider.System);
		cache.ReplaceSnapshot([certificate]);

		var services = new ServiceCollection();
		services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(PostgresConnectionString));
		services.AddDataProtection().PersistKeysToDbContext<ApplicationDbContext>();
		services.AddSingleton(cache);
		services.AddOptions<KeyManagementOptions>()
			.PostConfigure(options => options.XmlEncryptor = new CachedCertificateXmlEncryptor(cache));
		await using var provider = services.BuildServiceProvider();

		// 이 provider(=이 XmlEncryptor)로 키를 하나 새로 만들고, 그 키만 확인한다.
		var now = DateTimeOffset.UtcNow;
		var createdKey = provider.GetRequiredService<IKeyManager>().CreateNewKey(now, now.AddDays(1));

		var protector = provider.GetRequiredService<IDataProtectionProvider>()
			.CreateProtector("DataProtectionPersistenceInfraTests.WithCertificate");
		var protectedValue = protector.Protect("hello-with-certificate");
		Assert.Equal("hello-with-certificate", protector.Unprotect(protectedValue));

		await using var verifyDb = new ApplicationDbContext(
			new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(PostgresConnectionString).Options);
		var rows = await verifyDb.DataProtectionKeys.AsNoTracking().ToListAsync();
		var createdRow = Assert.Single(
			rows,
			r => r.Xml is not null && r.Xml.Contains(createdKey.KeyId.ToString(), StringComparison.Ordinal));

		Assert.Contains("encryptedSecret", createdRow.Xml);
		Assert.Contains(nameof(CachedCertificateXmlDecryptor), createdRow.Xml);
	}

	private static async Task<bool> IsPostgresAvailableAsync()
	{
		try
		{
			await using var connection = new NpgsqlConnection(PostgresConnectionString);
			await connection.OpenAsync();
			return true;
		}
		catch
		{
			return false;
		}
	}
}
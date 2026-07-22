using Microsoft.AspNetCore.Identity;
using S3EnvManager.Database;
using S3EnvManager.MigrationService;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddNpgsqlDbContext<ApplicationDbContext>("s3envmanagerdb");

// SchemaVersion은 Web과 반드시 같아야 한다(ApplicationIdentitySchema) - 다르면 모델이 어긋난다.
builder.Services.AddIdentityCore<ApplicationUser>(options =>
	{
		options.Stores.SchemaVersion = ApplicationIdentitySchema.Version;
	})
	.AddRoles<IdentityRole>()
	.AddEntityFrameworkStores<ApplicationDbContext>();

builder.Services.Configure<InitialAdminSetupOptions>(builder.Configuration.GetSection("InitialAdminSetup"));

builder.Services.AddHostedService<Worker>();

var host = builder.Build();

host.Run();
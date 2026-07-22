using Amazon;

var builder = DistributedApplication.CreateBuilder(args);

// `aspire publish -o docker-compose-artifacts`로 배포할 때만 docker-compose.yml 생성 대상.
builder.AddDockerComposeEnvironment("compose");

// 비밀번호를 고정하지 않으면 재기동마다 Aspire가 새 비밀번호를 생성하지만, WithDataVolume()의
// 기존 볼륨은 최초 initdb 시점 비밀번호를 유지해 인증 실패가 반복된다.
var postgresPassword = builder.AddParameter("postgres-password", secret: true);
var postgres = builder.AddPostgres("postgres", password: postgresPassword)
	.WithDataVolume()
	.WithPgAdmin();

var s3EnvManagerDb = postgres.AddDatabase("s3envmanagerdb");

// DataProtection PFX 비밀번호. DB 단독 유출로 복호화되지 않도록 DB 밖(user-secrets)에서 관리
// (Web/Program.cs 참고). 값이 없으면 Web이 인증서 보호를 끄고 동작해야 하는데, 값을 전혀
// 설정하지 않으면 Aspire가 대시보드 입력을 기다리며 기동이 멈춘다(실측). literal 기본값
// (value: "")으로는 해결되지 않아, 설정을 읽기 전에 키 자체를 빈 문자열로 미리 채워 우회한다.
if (string.IsNullOrEmpty(builder.Configuration["Parameters:dataprotection-cert-password"]))
{
	builder.Configuration["Parameters:dataprotection-cert-password"] = "";
}
var dataProtectionCertPassword = builder.AddParameter("dataprotection-cert-password", secret: true);

// DB 마이그레이션 + Identity 역할 시드를 전담하고 종료하는 워커. web은 WaitForCompletion으로
// exit 0까지 기동을 미룬다.
var migrations = builder.AddProject<Projects.S3EnvManager_MigrationService>("migrations")
	.WithEnvironment("TZ", "Asia/Seoul")
	.WithReference(s3EnvManagerDb)
	.WaitFor(s3EnvManagerDb);

// CMK/버킷이 ap-northeast-2에 있으므로 클라이언트 리전을 맞춘다 - KMS는 클라이언트 리전과
// CMK ARN 리전이 일치해야 호출이 성공한다.
var awsConfig = builder.AddAWSSDKConfig()
	.WithRegion(RegionEndpoint.APNortheast2);

var web = builder.AddProject<Projects.S3EnvManager_Web>("web")
	.WithEnvironment("DataProtectionCertificate__Password", dataProtectionCertPassword)
	.WithEnvironment("TZ", "Asia/Seoul")
	.WithReference(s3EnvManagerDb)
	.WithReference(awsConfig)
	.WaitFor(s3EnvManagerDb)
	.WaitForCompletion(migrations);

builder.Build().Run();
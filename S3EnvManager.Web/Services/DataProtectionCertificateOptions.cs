namespace S3EnvManager.Web.Services;

public sealed class DataProtectionCertificateOptions
{
	// appsettings 파일이 아니라 환경변수/Aspire 파라미터로만 주입한다 - 평문 저장 시
	// DB 저장 방식을 택한 의미가 없어진다.
	public string? Password { get; set; }

	public Int32 ValidityYears { get; set; } = 2;

	public Int32 RotateBeforeExpiryDays { get; set; } = 365;
}
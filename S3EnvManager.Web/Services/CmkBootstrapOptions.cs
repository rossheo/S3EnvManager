namespace S3EnvManager.Web.Services;

/// <summary>최초 admin/app CMK를 관리자 로그인 없이도 등록하기 위한 부트스트랩 설정.
/// appsettings의 "Cmk" 섹션에서 바인딩한다.</summary>
public sealed class CmkBootstrapOptions
{
	public string? AdminArn { get; set; }

	public string? AppArn { get; set; }
}
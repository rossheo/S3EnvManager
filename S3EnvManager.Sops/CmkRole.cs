namespace S3EnvManager.Sops;

/// <summary>CMK 역할. Admin=S3EnvManager 재편집 경로, App=Application 읽기 전용 복호화 경로.</summary>
public enum CmkRole
{
	Admin,
	App,
}
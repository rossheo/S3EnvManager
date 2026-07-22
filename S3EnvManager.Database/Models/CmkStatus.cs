namespace S3EnvManager.Database.Models;

/// <summary>role별로 정확히 하나의 CMK만 Active(새 암호화용)이고, 나머지는 Secondary
/// (복호화 전용)다.</summary>
public enum CmkStatus
{
	Active,
	Secondary,
}
using Microsoft.AspNetCore.Identity;

namespace S3EnvManager.Database;

/// <summary>Program.cs의 AddIdentityCore 설정과 ApplicationDbContextDesignTimeFactory가
/// 반드시 같은 값을 써야 한다 - 어긋나면 마이그레이션에서 테이블(예: AspNetUserPasskeys)이
/// 누락된다.</summary>
public static class ApplicationIdentitySchema
{
	public static readonly Version Version = IdentitySchemaVersions.Version3;
}
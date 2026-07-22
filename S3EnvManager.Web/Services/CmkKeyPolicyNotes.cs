using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public static class CmkKeyPolicyNotes
{
	public static string RoleLabel(CmkRole role) => role == CmkRole.Admin ? "Admin(주)" : "App(앱 전용)";
}

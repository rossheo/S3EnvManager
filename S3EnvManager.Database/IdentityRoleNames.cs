namespace S3EnvManager.Database;

public static class IdentityRoleNames
{
	public const string Administrator = "Administrator";
	public const string Member = "Member";
	public const string Guest = "Guest";

	public static readonly IReadOnlyList<string> All = [Administrator, Member, Guest];
}
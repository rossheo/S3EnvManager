using Microsoft.AspNetCore.Identity;

namespace S3EnvManager.Database;

public static class IdentityRoleSeeder
{
	public static async Task EnsureRolesSeededAsync(RoleManager<IdentityRole> roleManager)
	{
		foreach (var roleName in IdentityRoleNames.All)
		{
			if (!await roleManager.RoleExistsAsync(roleName))
			{
				await roleManager.CreateAsync(new IdentityRole(roleName));
			}
		}
	}
}
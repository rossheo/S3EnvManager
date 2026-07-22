namespace S3EnvManager.Database.Models;

public static class EnvNameExtensions
{
	public static string ToObjectSegment(this EnvName name) => name switch
	{
		EnvName.Dev => "dev",
		EnvName.Staging => "staging",
		EnvName.Product => "product",
		_ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
	};
}
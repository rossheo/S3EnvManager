using S3EnvManager.Sops;

namespace S3EnvManager.Database.Models;

/// <summary>role(admin/app)별로 1~N개의 CMK를 등록해두고, role 안에서 정확히 하나만
/// Active로 지정한다.</summary>
public class CmkRegistration
{
	public Guid CmkId { get; init; }

	public required string Arn { get; set; }

	public required CmkRole Role { get; init; }

	public required CmkStatus Status { get; set; }

	public DateTimeOffset CreatedAt { get; init; }
}
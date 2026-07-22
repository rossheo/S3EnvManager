namespace S3EnvManager.Database.Models;

/// <summary>여러 세대가 공존할 수 있고(주기적 로테이션), 각 SecretKey 암호문은 자신을
/// 암호화한 <see cref="KeyId"/>를 함께 저장해 어떤 세대로 복호화해야 하는지 스스로 가리킨다.</summary>
public class DataKeyGeneration
{
	public Guid KeyId { get; init; }

	/// <summary>KMS로 감싼 평문 데이터 키. 평문 자체는 저장하지 않고 기동 시 이 값을
	/// kms:Decrypt해서 메모리에만 캐싱한다.</summary>
	public required byte[] CiphertextBlob { get; set; }

	/// <summary>이 세대를 감싼 CMK - 항상 <see cref="Sops.CmkRole.Admin"/> role의 CMK다
	/// (SecretKey는 Application이 읽을 일이 없으므로 app role 엔트리 자체가 없다). CMK
	/// 레지스트리에서 이 CMK가 제거될 때 재래핑 대상이 되므로 <c>set</c>이다.</summary>
	public Guid CmkId { get; set; }

	public CmkRegistration? Cmk { get; init; }

	public DateTimeOffset CreatedAt { get; init; }
}
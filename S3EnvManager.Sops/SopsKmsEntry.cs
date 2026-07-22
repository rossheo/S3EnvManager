namespace S3EnvManager.Sops;

/// <summary>
/// sops 트레일러의 KMS 엔트리(`sops_kms__list_N__map_*`). sops 포맷에는 "역할" 개념이 없어
/// admin/app 구분은 <see cref="SopsEnvelopeCodec"/>가 쓰는 순서(index 0=Admin, 1=App)로만 관리된다.
/// </summary>
public sealed record SopsKmsEntry(
	string Arn,
	byte[] CiphertextBlob,
	DateTimeOffset CreatedAt,
	IReadOnlyDictionary<string, string> EncryptionContext);
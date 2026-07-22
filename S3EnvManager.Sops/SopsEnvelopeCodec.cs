namespace S3EnvManager.Sops;

/// <summary>
/// sops 통합의 최상위 진입점. 데이터 키를 primary(admin)/app-facing(app) 두 CMK로 각각 wrap해서
/// sops의 다중 KMS 엔트리로 기록한다. S3EnvManager는 admin 엔트리(index 0)만, Application은
/// app 엔트리(index 1)만 복호화할 권한을 갖는다는 전제다.
/// </summary>
public static class SopsEnvelopeCodec
{
	private const string EncryptionContextAppKey = "app";

	/// <summary>
	/// key=value 목록을 암호화해 sops dotenv 파일 내용을 만든다. 두 CMK를 최소 권한 자격증명
	/// (admin: GenerateDataKey, app: Encrypt만)으로 호출할 수 있도록 KMS 클라이언트를 role별로
	/// 분리해서 받는다.
	/// </summary>
	public static async Task<string> EncryptAsync(
		IEnumerable<KeyValuePair<string, string>> plaintextValues,
		string adminCmkArn,
		string appCmkArn,
		string appName,
		IKmsKeyOperations adminKms,
		IKmsKeyOperations appKms,
		CancellationToken cancellationToken = default)
	{
		var encryptionContext = new Dictionary<string, string> { [EncryptionContextAppKey] = appName };

		var (dataKey, adminCiphertext) = await adminKms.GenerateDataKeyAsync(
			adminCmkArn, encryptionContext, cancellationToken)
			.ConfigureAwait(false);
		var appCiphertext = await appKms.EncryptAsync(appCmkArn, dataKey, encryptionContext, cancellationToken)
			.ConfigureAwait(false);

		var document = new SopsDotEnvDocument
		{
			LastModified = DateTimeOffset.UtcNow,
		};

		var macCalculator = new SopsMacCalculator();
		foreach (var (key, plaintext) in plaintextValues)
		{
			macCalculator.Append(plaintext);
			var encryptedValue = SopsValueCipher.Encrypt(plaintext, dataKey, ValueAdditionalData(key));
			document.Entries.Add(new KeyValuePair<string, string>(key, encryptedValue));
		}

		document.KmsEntries.Add(
			new SopsKmsEntry(adminCmkArn, adminCiphertext, document.LastModified, encryptionContext));
		document.KmsEntries.Add(
			new SopsKmsEntry(appCmkArn, appCiphertext, document.LastModified, encryptionContext));

		var macPlaintext = macCalculator.ComputeHex();
		document.EncryptedMac = SopsValueCipher.Encrypt(
			macPlaintext, dataKey, MacAdditionalData(document.LastModified));

		return document.Serialize();
	}

	/// <summary>S3EnvManager 자신의 재편집 경로 - admin(primary) 엔트리(index 0)로 복호화한다.</summary>
	public static Task<Dictionary<string, string>> DecryptAsAdminAsync(
		string fileContent, IKmsKeyOperations kms, CancellationToken cancellationToken = default) =>
		DecryptAsync(fileContent, kmsEntryIndex: 0, kms, cancellationToken);

	/// <summary>Application의 읽기 경로 - app-facing 엔트리(index 1)로 복호화한다.</summary>
	public static Task<Dictionary<string, string>> DecryptAsAppAsync(
		string fileContent, IKmsKeyOperations kms, CancellationToken cancellationToken = default) =>
		DecryptAsync(fileContent, kmsEntryIndex: 1, kms, cancellationToken);

	/// <summary>
	/// 특정 인덱스의 KMS 엔트리로 복호화하는 저수준 API. CMK ARN은 항상 트레일러에 기록된 값
	/// (<see cref="SopsKmsEntry.Arn"/>)을 쓴다 - "현재 활성" ARN을 쓰면 CMK 승격/교체 후 옛
	/// 번들을 영영 못 여는 버그가 되므로, 옛 CMK가 레지스트리에 secondary로 남아 권한만
	/// 살아있으면 트레일러 ARN으로 복호화하는 것이 항상 옳다.
	/// </summary>
	public static async Task<Dictionary<string, string>> DecryptAsync(
		string fileContent, Int32 kmsEntryIndex, IKmsKeyOperations kms,
		CancellationToken cancellationToken = default)
	{
		var document = SopsDotEnvDocument.Parse(fileContent);
		if (kmsEntryIndex < 0 || kmsEntryIndex >= document.KmsEntries.Count)
		{
			throw new InvalidOperationException(
				$"KMS 엔트리 인덱스 {kmsEntryIndex}가 파일에 없습니다(엔트리 {document.KmsEntries.Count}개).");
		}

		var entry = document.KmsEntries[kmsEntryIndex];
		var dataKey = await kms.DecryptAsync(
			entry.Arn, entry.CiphertextBlob, entry.EncryptionContext, cancellationToken)
			.ConfigureAwait(false);

		var values = new Dictionary<string, string>();
		var macCalculator = new SopsMacCalculator();
		foreach (var (key, encryptedValue) in document.Entries)
		{
			var plaintext = SopsValueCipher.Decrypt(encryptedValue, dataKey, ValueAdditionalData(key));
			macCalculator.Append(plaintext);
			values[key] = plaintext;
		}

		var computedMac = macCalculator.ComputeHex();
		var fileMac = SopsValueCipher.Decrypt(
			document.EncryptedMac, dataKey, MacAdditionalData(document.LastModified));
		if (!string.Equals(fileMac, computedMac, StringComparison.Ordinal))
		{
			throw new CryptographicMacException(
				$"MAC이 일치하지 않습니다(위변조 의심). 파일: {fileMac}, 계산값: {computedMac}",
				innerException: new InvalidOperationException("MAC mismatch"));
		}

		return values;
	}

	private static string ValueAdditionalData(string key) => $"{key}:";

	private static string MacAdditionalData(DateTimeOffset lastModified) =>
		SopsDotEnvDocument.FormatRfc3339(lastModified);
}
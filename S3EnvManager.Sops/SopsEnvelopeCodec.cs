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
	/// 분리해서 받는다. 생성된 평문 데이터 키도 함께 반환한다 - 호출자가 직후 자체 검증을 위해
	/// 다시 KMS Decrypt를 부르지 않고 <see cref="DecryptWithDataKey"/>로 로컬 검증할 수 있도록.
	/// </summary>
	/// <param name="reusableDataKey">이미 감싸둔 데이터 키를 재사용한다(KMS 호출 0회). 재사용
	/// 여부와 한도는 이 패키지가 정하지 않는다 - 호출자(S3EnvManager.Web)의 정책이다. 넘긴 값이
	/// 이 <paramref name="appName"/>/CMK ARN 조합으로 감싼 것이 아니면, 트레일러가 실제 wrap과
	/// 다른 encryption context를 주장하게 되어 그 번들은 영구히 복호화 불가능해진다.</param>
	public static async Task<SopsEncryptResult> EncryptAsync(
		IEnumerable<KeyValuePair<string, string>> plaintextValues,
		string adminCmkArn,
		string appCmkArn,
		string appName,
		IKmsKeyOperations adminKms,
		IKmsKeyOperations appKms,
		SopsWrappedDataKey? reusableDataKey = null,
		CancellationToken cancellationToken = default)
	{
		var encryptionContext = new Dictionary<string, string> { [EncryptionContextAppKey] = appName };

		byte[] dataKey;
		byte[] adminCiphertext;
		byte[] appCiphertext;
		if (reusableDataKey is { } reused)
		{
			(dataKey, adminCiphertext, appCiphertext) =
				(reused.DataKey, reused.AdminCiphertext, reused.AppCiphertext);
		}
		else
		{
			(dataKey, adminCiphertext) = await adminKms.GenerateDataKeyAsync(
				adminCmkArn, encryptionContext, cancellationToken)
				.ConfigureAwait(false);
			appCiphertext = await appKms.EncryptAsync(appCmkArn, dataKey, encryptionContext, cancellationToken)
				.ConfigureAwait(false);
		}

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

		return new SopsEncryptResult(
			document.Serialize(), dataKey,
			new SopsWrappedDataKey(dataKey, adminCiphertext, appCiphertext));
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

		return DecryptWithDataKey(document, dataKey);
	}

	/// <summary>
	/// 이미 알고 있는 평문 데이터 키로 로컬에서 복호화한다(KMS 호출 없음). 저장 직후 자체 검증처럼
	/// 같은 호출 안에서 <see cref="EncryptAsync"/>가 이미 만든 데이터 키를 그대로 아는 경우에만
	/// 쓴다 - 다른 곳에서 받은 데이터 키를 신뢰하고 복호화하는 것이므로 호출자가 그 출처를
	/// 책임진다. KMS 트레일러 자체는 건드리지 않으므로(값/MAC만 검증) 트레일러 손상 여부까지
	/// 확인하려면 <see cref="DecryptWithDataKeyAndVerifyTrailer"/>를 쓴다.
	/// </summary>
	public static Dictionary<string, string> DecryptWithDataKey(string fileContent, byte[] dataKey) =>
		DecryptWithDataKey(SopsDotEnvDocument.Parse(fileContent), dataKey);

	/// <summary>
	/// <see cref="DecryptWithDataKey(string,byte[])"/>와 같지만, 트레일러가 정확히 admin/app
	/// 엔트리 2개를 예상한 ARN·비어있지 않은 ciphertext로 담고 있는지도 함께 확인한다. 저장 직후
	/// 자체 검증에 쓴다 - "값은 맞지만 트레일러가 깨져서 아무도 다시 열 수 없는 번들"을 잡아내기
	/// 위한 것으로, KMS Decrypt로 트레일러를 실제로 여는 대신 구조만 확인한다(트레일러의
	/// ciphertext blob 자체는 이번 저장에서 막 KMS로 만든 것이라 신뢰할 수 있음).
	/// </summary>
	public static Dictionary<string, string> DecryptWithDataKeyAndVerifyTrailer(
		string fileContent, byte[] dataKey, string expectedAdminCmkArn, string expectedAppCmkArn)
	{
		var document = SopsDotEnvDocument.Parse(fileContent);
		var isValidTrailer = document.KmsEntries.Count == 2
			&& document.KmsEntries[0].Arn == expectedAdminCmkArn
			&& document.KmsEntries[0].CiphertextBlob.Length > 0
			&& document.KmsEntries[1].Arn == expectedAppCmkArn
			&& document.KmsEntries[1].CiphertextBlob.Length > 0;
		if (!isValidTrailer)
		{
			throw new InvalidOperationException(
				"저장된 KMS 트레일러가 예상과 다릅니다(엔트리 손상/누락 의심).");
		}

		return DecryptWithDataKey(document, dataKey);
	}

	private static Dictionary<string, string> DecryptWithDataKey(SopsDotEnvDocument document, byte[] dataKey)
	{
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

/// <summary><see cref="SopsEnvelopeCodec.EncryptAsync"/>의 결과 - 암호화된 파일 내용과, 자체
/// 검증을 KMS 재호출 없이 로컬에서 할 수 있도록 생성된 평문 데이터 키를 함께 담는다.</summary>
public sealed record SopsEncryptResult(string Content, byte[] DataKey, SopsWrappedDataKey WrappedDataKey);

/// <summary>평문 데이터 키와, 그것을 admin/app CMK로 각각 감싼 ciphertext 한 벌.
/// <see cref="SopsEnvelopeCodec.EncryptAsync"/>에 되돌려주면 KMS를 다시 부르지 않는다.
///
/// 이 한 벌은 감쌀 때 쓴 (adminCmkArn, appCmkArn, appName) 조합에만 유효하다 - encryption
/// context에 appName이 들어가므로 다른 App에 쓰면 트레일러가 거짓 context를 주장하게 되어
/// 그 번들은 영구히 복호화 불가능해진다.</summary>
public sealed record SopsWrappedDataKey(byte[] DataKey, byte[] AdminCiphertext, byte[] AppCiphertext);
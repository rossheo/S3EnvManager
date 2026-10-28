namespace S3EnvManager.Sops.Tests;

public class SopsEnvelopeCodecTests
{
	private const string AdminCmkArn = "arn:aws:kms:us-east-1:000000000000:key/admin-test-key";
	private const string AppCmkArn = "arn:aws:kms:us-east-1:000000000000:key/app-test-key";

	[Fact]
	public async Task EncryptThenDecryptAsAdmin_ReturnsOriginalValues()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar", ["DB_PASSWORD"] = "hunter2" };

		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;
		var decrypted = await SopsEnvelopeCodec.DecryptAsAdminAsync(fileContent, kms);

		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task EncryptThenDecryptAsApp_ReturnsOriginalValues()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar", ["DB_PASSWORD"] = "hunter2" };

		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;
		var decrypted = await SopsEnvelopeCodec.DecryptAsAppAsync(fileContent, kms);

		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task Entries_AreIndependentlyWrappedByTheirOwnCmk_NotByAnExternallySuppliedArn()
	{
		// admin/app 엔트리가 서로 다른 CMK, 다른 ciphertext로 독립 wrap됐는지만 구조적으로 확인.
		// 실제 IAM 권한 분리는 AWS가 강제하며 Web.Tests의 실인프라 테스트가 검증한다.
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var document = SopsDotEnvDocument.Parse(fileContent);
		Assert.Equal(AdminCmkArn, document.KmsEntries[0].Arn);
		Assert.Equal(AppCmkArn, document.KmsEntries[1].Arn);
		Assert.NotEqual(document.KmsEntries[0].CiphertextBlob, document.KmsEntries[1].CiphertextBlob);
	}

	[Fact]
	public async Task DecryptAsAdmin_UsesArnStoredInTrailer_SurvivesCmkPromotionOfNewerBundles()
	{
		// admin CMK 승격 후에도 예전 CMK로 감싼 번들이 열려야 한다 - 트레일러 ARN을 쓰는지 확인.
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var decrypted = await SopsEnvelopeCodec.DecryptAsAdminAsync(fileContent, kms);
		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task TamperedValue_FailsMacVerification()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var tampered = Tamper(fileContent);

		await Assert.ThrowsAsync<CryptographicMacException>(
			() => SopsEnvelopeCodec.DecryptAsAdminAsync(tampered, kms));
	}

	[Fact]
	public async Task DecryptWithDataKey_MatchesKmsDecrypt_WithoutCallingKms()
	{
		// SecretBundleService.SaveAsync가 저장 직후 자체 검증할 때 KMS를 다시 부르지 않고
		// EncryptAsync가 반환한 평문 데이터 키로 로컬 검증하는 경로 - 결과가 KMS 왕복 복호화와
		// 같아야 한다.
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar", ["DB_PASSWORD"] = "hunter2" };
		var result = await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var decrypted = SopsEnvelopeCodec.DecryptWithDataKey(result.Content, result.DataKey);

		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task DecryptWithDataKey_TamperedValue_FailsMacVerification()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var result = await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var tampered = Tamper(result.Content);

		Assert.Throws<CryptographicMacException>(
			() => SopsEnvelopeCodec.DecryptWithDataKey(tampered, result.DataKey));
	}

	// 값/MAC은 멀쩡한데 트레일러(admin ARN)만 바뀐 경우 - DecryptWithDataKey는(트레일러를 보지
	// 않으므로) 여전히 성공해야 하고, VerifyTrailer 버전만 이를 잡아내야 한다. 이 대비가 트레일러
	// 검증이 실제로 그 트레일러를 확인하고 있다는 것을 증명한다(값 비교만으로 우연히 통과한 게 아님).
	[Fact]
	public async Task DecryptWithDataKeyAndVerifyTrailer_TamperedAdminArn_ThrowsEvenThoughValuesAndMacAreIntact()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var result = await SopsEnvelopeCodec.EncryptAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var document = SopsDotEnvDocument.Parse(result.Content);
		document.KmsEntries[0] = document.KmsEntries[0] with
			{ Arn = document.KmsEntries[0].Arn + "-tampered" };
		var tamperedTrailer = document.Serialize();

		var decrypted = SopsEnvelopeCodec.DecryptWithDataKey(tamperedTrailer, result.DataKey);
		Assert.Equal(original, decrypted);

		Assert.Throws<InvalidOperationException>(() =>
			SopsEnvelopeCodec.DecryptWithDataKeyAndVerifyTrailer(
				tamperedTrailer, result.DataKey, AdminCmkArn, AppCmkArn));
	}

	[Fact]
	public async Task EncryptLinesThenDecryptLinesAsAdmin_PreservesCommentsInOrder()
	{
		var kms = new FakeKmsKeyOperations();
		List<SopsDotEnvLine> original =
		[
			SopsDotEnvLine.Comment(" 접속 정보"),
			SopsDotEnvLine.Comment("# security type: SASL"),
			SopsDotEnvLine.Entry("FOO", "bar"),
			SopsDotEnvLine.Comment(""),
			SopsDotEnvLine.Entry("DB_PASSWORD", "hunter2"),
			SopsDotEnvLine.Comment(" 끝"),
		];

		var fileContent = (await SopsEnvelopeCodec.EncryptLinesAsync(
			original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		// sops와 같이 주석도 암호화되어 평문이 파일에 남지 않아야 한다.
		Assert.DoesNotContain("접속 정보", fileContent);
		Assert.Contains("type:comment]", fileContent);
		Assert.Equal(original, await SopsEnvelopeCodec.DecryptLinesAsAdminAsync(fileContent, kms));
	}

	[Fact]
	public async Task DecryptAsApp_IgnoresComments_ReturnsOnlyValues()
	{
		var kms = new FakeKmsKeyOperations();
		var fileContent = (await SopsEnvelopeCodec.EncryptLinesAsync(
			[SopsDotEnvLine.Comment(" note"), SopsDotEnvLine.Entry("FOO", "bar")],
			AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var decrypted = await SopsEnvelopeCodec.DecryptAsAppAsync(fileContent, kms);

		Assert.Equal(new Dictionary<string, string> { ["FOO"] = "bar" }, decrypted);
	}

	// sops는 주석을 MAC에 넣지 않는다 - 주석이 있든 없든 같은 값이면 MAC 검증이 통과해야 한다.
	[Fact]
	public async Task Comments_AreExcludedFromMac()
	{
		var kms = new FakeKmsKeyOperations();
		var result = await SopsEnvelopeCodec.EncryptLinesAsync(
			[SopsDotEnvLine.Comment(" note"), SopsDotEnvLine.Entry("FOO", "bar")],
			AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var withoutComment = string.Join('\n',
			result.Content.Split('\n').Where(l => !l.StartsWith('#')));

		Assert.Equal(new Dictionary<string, string> { ["FOO"] = "bar" },
			SopsEnvelopeCodec.DecryptWithDataKey(withoutComment, result.DataKey));
	}

	// 평문 주석(옛 sops 버전이나 손으로 넣은 주석)은 sops처럼 복호화 실패 시 원문 그대로 둔다.
	[Fact]
	public async Task DecryptLinesAsAdmin_PlaintextComment_IsKeptAsIs()
	{
		var kms = new FakeKmsKeyOperations();
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(
			new Dictionary<string, string> { ["FOO"] = "bar" },
			AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var lines = await SopsEnvelopeCodec.DecryptLinesAsAdminAsync("# plain note\n" + fileContent, kms);

		Assert.Equal([SopsDotEnvLine.Comment(" plain note"), SopsDotEnvLine.Entry("FOO", "bar")], lines);
	}

	// CMK 제거 시 재래핑 경로는 Parse -> Serialize만 하므로 암호화된 주석 줄이 그대로 보존돼야 한다.
	[Fact]
	public async Task ParseThenSerialize_PreservesEncryptedCommentLines()
	{
		var kms = new FakeKmsKeyOperations();
		var fileContent = (await SopsEnvelopeCodec.EncryptLinesAsync(
			[SopsDotEnvLine.Comment(" note"), SopsDotEnvLine.Entry("FOO", "bar")],
			AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		Assert.Equal(fileContent, SopsDotEnvDocument.Parse(fileContent).Serialize());
	}

	private static string Tamper(string fileContent)
	{
		var tampered = fileContent.Replace("data:bI4J", "data:xxxx", StringComparison.Ordinal);
		if (tampered != fileContent)
		{
			return tampered;
		}

		// data:가 매번 랜덤이라 위 치환이 안 맞을 수 있어 첫 ENC[...] 줄의 한 글자를 뒤집는다.
		var lines = fileContent.Split('\n');
		var index = Array.FindIndex(lines, l => l.StartsWith("FOO=ENC[", StringComparison.Ordinal));
		Assert.True(index >= 0);
		var dataStart = lines[index].IndexOf("data:", StringComparison.Ordinal) + "data:".Length;
		var chars = lines[index].ToCharArray();
		chars[dataStart] = chars[dataStart] == 'A' ? 'B' : 'A';
		lines[index] = new string(chars);
		return string.Join('\n', lines);
	}
}
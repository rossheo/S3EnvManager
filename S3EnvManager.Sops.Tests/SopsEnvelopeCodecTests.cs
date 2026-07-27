using S3EnvManager.Sops;
using Xunit;

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

		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;
		var decrypted = await SopsEnvelopeCodec.DecryptAsAdminAsync(fileContent, kms);

		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task EncryptThenDecryptAsApp_ReturnsOriginalValues()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar", ["DB_PASSWORD"] = "hunter2" };

		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;
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
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

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
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

		var decrypted = await SopsEnvelopeCodec.DecryptAsAdminAsync(fileContent, kms);
		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task TamperedValue_FailsMacVerification()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var fileContent = (await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms)).Content;

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
		var result = await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var decrypted = SopsEnvelopeCodec.DecryptWithDataKey(result.Content, result.DataKey);

		Assert.Equal(original, decrypted);
	}

	[Fact]
	public async Task DecryptWithDataKey_TamperedValue_FailsMacVerification()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var result = await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var tampered = Tamper(result.Content);

		Assert.Throws<CryptographicMacException>(() => SopsEnvelopeCodec.DecryptWithDataKey(tampered, result.DataKey));
	}

	// 값/MAC은 멀쩡한데 트레일러(admin ARN)만 바뀐 경우 - DecryptWithDataKey는(트레일러를 보지
	// 않으므로) 여전히 성공해야 하고, VerifyTrailer 버전만 이를 잡아내야 한다. 이 대비가 트레일러
	// 검증이 실제로 그 트레일러를 확인하고 있다는 것을 증명한다(값 비교만으로 우연히 통과한 게 아님).
	[Fact]
	public async Task DecryptWithDataKeyAndVerifyTrailer_TamperedAdminArn_ThrowsEvenThoughValuesAndMacAreIntact()
	{
		var kms = new FakeKmsKeyOperations();
		var original = new Dictionary<string, string> { ["FOO"] = "bar" };
		var result = await SopsEnvelopeCodec.EncryptAsync(original, AdminCmkArn, AppCmkArn, "myapp", kms, kms);

		var document = SopsDotEnvDocument.Parse(result.Content);
		document.KmsEntries[0] = document.KmsEntries[0] with { Arn = document.KmsEntries[0].Arn + "-tampered" };
		var tamperedTrailer = document.Serialize();

		var decrypted = SopsEnvelopeCodec.DecryptWithDataKey(tamperedTrailer, result.DataKey);
		Assert.Equal(original, decrypted);

		Assert.Throws<InvalidOperationException>(() =>
			SopsEnvelopeCodec.DecryptWithDataKeyAndVerifyTrailer(
				tamperedTrailer, result.DataKey, AdminCmkArn, AppCmkArn));
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
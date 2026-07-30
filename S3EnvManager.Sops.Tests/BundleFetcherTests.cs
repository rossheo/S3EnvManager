using S3EnvManager.Cli;
using S3EnvManager.Sops;
using Xunit;

namespace S3EnvManager.Sops.Tests;

public class BundleFetcherTests
{
	private const string Bucket = "test-bucket";
	private const string AppName = "myapp";
	private const string EnvSegment = "product";
	private const string AdminCmkArn = "arn:aws:kms:us-east-1:000000000000:key/admin-test-key";
	private const string AppCmkArn = "arn:aws:kms:us-east-1:000000000000:key/app-test-key";

	private static string BaseKey => $"{AppName}/{EnvSegment}.env";
	private static string OverwriteKey => $"{AppName}/{EnvSegment}.overwrite.env";

	private static async Task<string> EncryptAsync(
		FakeKmsKeyOperations kms, IDictionary<string, string> values) =>
		(await SopsEnvelopeCodec.EncryptAsync(values, AdminCmkArn, AppCmkArn, AppName, kms, kms)).Content;

	[Fact]
	public async Task FetchAsync_BaseOnly_ReturnsBaseValues()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetObject(BaseKey, await EncryptAsync(kms, new Dictionary<string, string> { ["FOO"] = "base-foo" }));

		var result = await new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None);

		Assert.Equal("base-foo", result.Values["FOO"]);
		Assert.True(result.BaseBundleFound);
		Assert.False(result.OverwriteBundleFound);
		Assert.Null(result.Warning);
	}

	[Fact]
	public async Task FetchAsync_BaseAndOverwrite_OverwriteWinsPerKey()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetObject(BaseKey, await EncryptAsync(kms,
			new Dictionary<string, string> { ["FOO"] = "base-foo", ["BAR"] = "base-bar" }));
		store.SetObject(OverwriteKey, await EncryptAsync(kms,
			new Dictionary<string, string> { ["FOO"] = "overwrite-foo" }));

		var result = await new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None);

		Assert.Equal("overwrite-foo", result.Values["FOO"]);
		Assert.Equal("base-bar", result.Values["BAR"]);
		Assert.True(result.BaseBundleFound);
		Assert.True(result.OverwriteBundleFound);
		Assert.Null(result.Warning);
	}

	[Fact]
	public async Task FetchAsync_BaseMissing_WithoutAllowMissingBundle_ThrowsExitCode3()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.BundleMissing, ex.ExitCode);
	}

	[Fact]
	public async Task FetchAsync_BaseMissing_WithAllowMissingBundle_AndNoOverwrite_ReturnsEmptyWithoutWarning()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();

		var result = await new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: true, CancellationToken.None);

		Assert.Empty(result.Values);
		Assert.False(result.BaseBundleFound);
		Assert.False(result.OverwriteBundleFound);
		Assert.Null(result.Warning);
	}

	[Fact]
	public async Task FetchAsync_BaseMissing_ButOverwritePresent_WarnsEvenThoughAllowed()
	{
		// base보다 overwrite를 먼저 만든 실수 상황 - --allow-missing-bundle이 있어도
		// 조용히 넘어가지 않고 경고를 남긴다(exit code는 그대로 0/성공).
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetObject(OverwriteKey, await EncryptAsync(kms,
			new Dictionary<string, string> { ["FOO"] = "overwrite-foo" }));

		var result = await new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: true, CancellationToken.None);

		Assert.Equal("overwrite-foo", result.Values["FOO"]);
		Assert.False(result.BaseBundleFound);
		Assert.True(result.OverwriteBundleFound);
		Assert.NotNull(result.Warning);
	}

	[Fact]
	public async Task FetchAsync_ForbiddenOnBase_ThrowsExitCode2()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetForbidden(BaseKey);

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.AwsPermissionDenied, ex.ExitCode);
	}

	[Fact]
	public async Task FetchAsync_MacTampered_ThrowsExitCode4()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		var content = await EncryptAsync(kms, new Dictionary<string, string> { ["FOO"] = "bar" });
		store.SetObject(BaseKey, Tamper(content));

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.IntegrityFailure, ex.ExitCode);
	}

	[Fact]
	public async Task FetchAsync_RealKmsAccessDeniedException_ThrowsExitCode4()
	{
		// 트레일러 자체는 멀쩡하지만(엔트리 2개), 실제 KMS Decrypt 호출이
		// AmazonKeyManagementServiceException 계열(AccessDeniedException 등)로 실패하는
		// 상황을 재현한다 - InvalidOperationException 하나로 뭉뚱그리면 무관한 프로그래밍
		// 오류까지 "변조 의심"으로 오분류하게 되므로, 실제 KMS 예외 타입으로 이 경로를
		// 별도 검증한다(코드 리뷰에서 지적됨).
		var encryptingKms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetObject(BaseKey, await EncryptAsync(encryptingKms, new Dictionary<string, string> { ["FOO"] = "bar" }));

		var decryptingKms = new ThrowingKmsKeyOperations();

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, decryptingKms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.IntegrityFailure, ex.ExitCode);
	}

	[Fact]
	public async Task FetchAsync_TrailerHasFewerThanTwoKmsEntries_ThrowsExitCode4()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		var content = await EncryptAsync(kms, new Dictionary<string, string> { ["FOO"] = "bar" });
		var document = SopsDotEnvDocument.Parse(content);
		document.KmsEntries.RemoveAt(1);
		store.SetObject(BaseKey, document.Serialize());

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.IntegrityFailure, ex.ExitCode);
	}

	[Fact]
	public async Task FetchAsync_UnclassifiedAwsServiceFailure_ThrowsExitCode6()
	{
		var kms = new FakeKmsKeyOperations();
		var store = new FakeBundleObjectStore();
		store.SetServiceUnavailable(BaseKey);

		var ex = await Assert.ThrowsAsync<CliException>(() => new BundleFetcher(store, kms)
			.FetchAsync(Bucket, AppName, EnvSegment, allowMissingBundle: false, CancellationToken.None));

		Assert.Equal(ExitCode.AwsCallFailed, ex.ExitCode);
	}

	/// <summary>실제 KMS AccessDeniedException(AmazonKeyManagementServiceException 계열)을
	/// 그대로 재현하는 가짜 - FakeKmsKeyOperations의 InvalidOperationException과는 다른
	/// 예외 타입 경로를 검증하기 위한 전용 더블.</summary>
	private sealed class ThrowingKmsKeyOperations : IKmsKeyOperations
	{
		public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
			string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("이 테스트 더블은 Decrypt만 지원합니다.");

		public Task<byte[]> EncryptAsync(
			string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			throw new NotSupportedException("이 테스트 더블은 Decrypt만 지원합니다.");

		public Task<byte[]> DecryptAsync(
			string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
			CancellationToken cancellationToken = default) =>
			// KMS SDK는 AccessDenied 전용 하위 타입을 따로 두지 않고 기반 타입에 ErrorCode로
			// 표현한다 - 실제 AWS 호출 실패를 그대로 재현하기 위해 같은 기반 타입을 쓴다.
			throw new Amazon.KeyManagementService.AmazonKeyManagementServiceException(
				"가짜 CMK 접근 거부", Amazon.Runtime.ErrorType.Sender, "AccessDeniedException", "req-id",
				System.Net.HttpStatusCode.Forbidden);
	}

	private static string Tamper(string fileContent)
	{
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

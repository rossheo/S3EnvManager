using S3EnvManager.Sops;

namespace S3EnvManager.Web.Tests;

/// <summary>Decrypt 시 호출자가 넘긴 cmkArn이 blob을 감싼 ARN과 다르면 예외를 던져,
/// "활성 ARN으로 복호화를 시도하면 실패해야 한다"는 재래핑 회귀를 의도적으로 실 KMS보다
/// 엄격하게 재현한다.
///
/// 감싼 ciphertext 저장소는 인스턴스가 아니라 프로세스 전체에서 공유되는 static이어야 한다 -
/// 실 KMS는 계정 공유 서비스라 어떤 클라이언트 인스턴스로도 복호화 가능한데, 다른 테스트
/// 클래스가 만든 행을 이어받아 복호화하는 경우가 있어 이 특성을 유지해야 한다(직렬 실행이라 안전).</summary>
public sealed class FakeKmsKeyOperations : IKmsKeyOperations
{
	private sealed record Wrapped(string CmkArn, byte[] PlaintextKey, Dictionary<string, string> Context);

	private static readonly Dictionary<Int32, Wrapped> WrappedById = [];
	private static Int32 nextId;

	public Task<(byte[] PlaintextKey, byte[] CiphertextBlob)> GenerateDataKeyAsync(
		string cmkArn, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var plaintextKey = new byte[32];
		Random.Shared.NextBytes(plaintextKey);
		var blob = Store(cmkArn, plaintextKey, encryptionContext);
		return Task.FromResult((plaintextKey, blob));
	}

	public Task<byte[]> EncryptAsync(
		string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default) =>
		Task.FromResult(Store(cmkArn, plaintextKey, encryptionContext));

	public Task<byte[]> DecryptAsync(
		string cmkArn, byte[] ciphertextBlob, IReadOnlyDictionary<string, string> encryptionContext,
		CancellationToken cancellationToken = default)
	{
		var id = BitConverter.ToInt32(ciphertextBlob);
		if (!WrappedById.TryGetValue(id, out var entry))
		{
			throw new InvalidOperationException("알 수 없는 ciphertext blob입니다(FakeKmsKeyOperations 인스턴스가 " +
				"이 데이터를 감쌀 때와 다릅니다).");
		}
		if (entry.CmkArn != cmkArn)
		{
			throw new InvalidOperationException(
				$"이 데이터는 {entry.CmkArn}(으)로 감싸져 있어 {cmkArn}(으)로 복호화할 수 없습니다 " +
				"(실 KMS의 IncorrectKeyException을 흉내냄).");
		}
		if (!ContextEquals(entry.Context, encryptionContext))
		{
			throw new InvalidOperationException("encryption context가 일치하지 않습니다.");
		}
		return Task.FromResult(entry.PlaintextKey);
	}

	private static byte[] Store(string cmkArn, byte[] plaintextKey, IReadOnlyDictionary<string, string> context)
	{
		var id = Interlocked.Increment(ref nextId);
		WrappedById[id] = new Wrapped(cmkArn, plaintextKey, new Dictionary<string, string>(context));
		return BitConverter.GetBytes(id);
	}

	private static bool ContextEquals(
		IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
	{
		if (a.Count != b.Count)
		{
			return false;
		}
		foreach (var (key, value) in a)
		{
			if (!b.TryGetValue(key, out var otherValue) || otherValue != value)
			{
				return false;
			}
		}
		return true;
	}
}

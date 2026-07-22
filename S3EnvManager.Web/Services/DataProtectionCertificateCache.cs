using System.Security.Cryptography.X509Certificates;

namespace S3EnvManager.Web.Services;

/// <summary>IXmlEncryptor/IXmlDecryptor는 동기 인터페이스라 그 안에서 EF Core 비동기 조회를
/// 하지 않기 위한 캐시 계층이다.</summary>
public sealed class DataProtectionCertificateCache
{
	private IReadOnlyList<X509Certificate2> _snapshot = [];

	// NotBefore가 가장 늦은 것이 곧 활성 인증서(발급 즉시 유효하도록 만들기 때문).
	public X509Certificate2? GetActive() =>
		Volatile.Read(ref _snapshot) is { Count: > 0 } snapshot ? snapshot[0] : null;

	public IReadOnlyList<X509Certificate2> GetAll() => Volatile.Read(ref _snapshot);

	// NotBefore 내림차순으로 저장해야 GetActive()가 인덱스 0을 그대로 쓸 수 있다.
	public void ReplaceSnapshot(IReadOnlyList<X509Certificate2> certificates)
	{
		var ordered = certificates.OrderByDescending(c => c.NotBefore).ToArray();
		Volatile.Write(ref _snapshot, ordered);
	}
}
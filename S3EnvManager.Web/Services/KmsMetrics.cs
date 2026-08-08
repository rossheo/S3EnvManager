using System.Diagnostics.Metrics;

namespace S3EnvManager.Web.Services;

// KMS free tier(월 2만 요청)에 얼마나 근접했는지를 AWS 콘솔 밖에서 알 방법이 없었다.
// 절감 조치를 넣기 전에 먼저 세는 것이 순서다 - 여기서 세는 것은 "실제로 AWS까지 나간 호출"이라,
// Decrypt 캐시가 흡수한 요청은 kms.calls에 잡히지 않고 decrypt_cache{result=hit}로만 잡힌다.
//
// Web의 모든 KMS 트래픽은 CachingKmsKeyOperations를 지난다(Program.cs가 admin/app 양쪽 다
// 그렇게 등록한다) - 이 데코레이터 한 곳만 계측하면 누락이 없다. AwsKmsKeyOperations는
// 제3자가 설치하는 S3EnvManager.Sops 패키지에 있어 계측을 넣지 않는다.
public sealed class KmsMetrics
{
	public const string MeterName = "S3EnvManager.Kms";

	private readonly Counter<Int64> _calls;
	private readonly Counter<Int64> _decryptCache;
	private readonly Counter<Int64> _dataKeyReuse;

	public KmsMetrics(IMeterFactory meterFactory)
	{
		var meter = meterFactory.Create(MeterName);
		_calls = meter.CreateCounter<Int64>(
			"s3envmanager.kms.calls", unit: "{call}",
			description: "실제로 AWS KMS까지 나간 호출 수(캐시 적중은 제외).");
		_decryptCache = meter.CreateCounter<Int64>(
			"s3envmanager.kms.decrypt_cache", unit: "{lookup}",
			description: "Decrypt 캐시 조회 결과(hit/miss).");
		_dataKeyReuse = meter.CreateCounter<Int64>(
			"s3envmanager.kms.datakey_reuse", unit: "{lookup}",
			description: "번들 저장 시 감싼 데이터 키 재사용 결과(hit이면 그 저장은 KMS를 0회 쓴다).");
	}

	public void RecordGenerateDataKey() => _calls.Add(1, new KeyValuePair<string, object?>("operation", "generate_data_key"));

	public void RecordEncrypt() => _calls.Add(1, new KeyValuePair<string, object?>("operation", "encrypt"));

	public void RecordDecrypt() => _calls.Add(1, new KeyValuePair<string, object?>("operation", "decrypt"));

	public void RecordDecryptCacheHit() => _decryptCache.Add(1, new KeyValuePair<string, object?>("result", "hit"));

	public void RecordDecryptCacheMiss() => _decryptCache.Add(1, new KeyValuePair<string, object?>("result", "miss"));

	public void RecordDataKeyReuseHit() => _dataKeyReuse.Add(1, new KeyValuePair<string, object?>("result", "hit"));

	public void RecordDataKeyReuseMiss() => _dataKeyReuse.Add(1, new KeyValuePair<string, object?>("result", "miss"));
}

using System.Net;
using Amazon.KeyManagementService;
using Amazon.Runtime;
using Amazon.S3;
using S3EnvManager.Sops;

namespace S3EnvManager.Cli;

/// <summary>base+overwrite 번들을 읽어 병합한 유효값을 만든다. PLAN-cli.md "명령어 설계"
/// 절의 병합 규칙(overwrite가 base를 키 단위로 덮어씀)과 "종료 코드" 절의 예외→exit 코드
/// 매핑을 그대로 구현한다.
///
/// 오브젝트 키 조립(`{app}/{env}{.overwrite}?.env`)은 provider(Configuration)/Web의
/// SecretObjectKeys와 별개로 여기서도 구현한다 - 인지된 부채: 접미사 규칙을 바꿀 일이
/// 생기면 이 세 곳(SecretObjectKeys, S3EnvManagerConfigurationProvider, 여기)을 함께
/// 고쳐야 한다.</summary>
public sealed class BundleFetcher(IBundleObjectStore store, IKmsKeyOperations kms)
{
	public async Task<BundleFetchResult> FetchAsync(
		string bucket, string appName, string envSegment, bool allowMissingBundle,
		CancellationToken cancellationToken)
	{
		var baseKey = $"{appName}/{envSegment}.env";
		var overwriteKey = $"{appName}/{envSegment}.overwrite.env";

		var (baseValues, baseFound) = await TryReadBundleAsync(bucket, baseKey, cancellationToken)
			.ConfigureAwait(false);
		if (!baseFound && !allowMissingBundle)
		{
			throw new CliException(ExitCode.BundleMissing, $"'{baseKey}'가 S3에 없습니다.");
		}

		var (overwriteValues, overwriteFound) = await TryReadBundleAsync(bucket, overwriteKey, cancellationToken)
			.ConfigureAwait(false);

		var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
		foreach (var (key, value) in baseValues)
		{
			merged[key] = value;
		}
		foreach (var (key, value) in overwriteValues)
		{
			merged[key] = value;
		}

		// base가 없고 overwrite만 있는 조합은 --allow-missing-bundle로도 침묵시키지
		// 않는다 - 오버라이드를 base보다 먼저 만든 실수일 가능성이 높다(PLAN-cli.md
		// "명령어 설계" 절 참고). exit code는 그대로 0으로 두고 경고만 남긴다.
		string? warning = !baseFound && overwriteFound
			? $"경고: base 번들('{baseKey}')은 없는데 overwrite 번들('{overwriteKey}')만 " +
				"존재합니다 - 거의 항상 실수입니다(오버라이드를 base보다 먼저 만든 경우 등)."
			: null;

		return new BundleFetchResult(merged, baseFound, overwriteFound, warning);
	}

	private async Task<(IReadOnlyDictionary<string, string> Values, bool Found)> TryReadBundleAsync(
		string bucket, string key, CancellationToken cancellationToken)
	{
		string content;
		try
		{
			content = await store.GetObjectContentAsync(bucket, key, cancellationToken).ConfigureAwait(false);
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
		{
			return (EmptyValues, false);
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden)
		{
			// s3:ListBucket이 없는 자격증명에서는 존재하지 않는 오브젝트의 GetObject도
			// 403으로 보인다(AWS 의도된 동작) - IamAppCredentialProvisioner가 발급한
			// 자격증명(자기 prefix ListBucket 포함)을 쓴다는 전제 하에서만 여기 도달이
			// "진짜 권한 없음"을 뜻한다. PLAN-cli.md "범위" 절 참고.
			throw new CliException(
				ExitCode.AwsPermissionDenied,
				$"'{key}' 접근이 거부되었습니다(403) - 자격증명에 s3:GetObject 권한이 없거나, " +
				"s3:ListBucket이 없는 자격증명이라면 사실은 번들이 없는 것일 수도 있습니다 " +
				"(IamAppCredentialProvisioner로 발급된 자격증명인지 먼저 확인하세요).",
				ex);
		}
		catch (AmazonServiceException ex)
		{
			// 403/404 외의 나머지(5xx, 스로틀링, 만료된 토큰 등) - 처리되지 않은 예외로
			// 스택트레이스가 새는 대신 분류된 종료 코드로 실패시킨다.
			throw new CliException(
				ExitCode.AwsCallFailed, $"'{key}' 조회 중 AWS 호출이 실패했습니다: {ex.Message}", ex);
		}

		// KMS를 부르기 전에 트레일러 모양부터 확인한다 - DecryptAsAppAsync는 엔트리가
		// 2개 미만이면 InvalidOperationException을 던지는데, 그 타입은 진짜 프로그래밍
		// 오류와 구분이 안 돼서 여기서 캐치하면 무관한 버그까지 "변조 의심"으로 오분류하게
		// 된다(코드 리뷰에서 지적됨). 트레일러 손상은 여기서 미리 잡아 exit 4로 명시적으로
		// 매핑하고, DecryptAsAppAsync 쪽 catch는 실제 KMS/MAC 실패만 남긴다.
		SopsDotEnvDocument document;
		try
		{
			document = SopsDotEnvDocument.Parse(content);
		}
		catch (Exception ex)
		{
			throw new CliException(
				ExitCode.IntegrityFailure, $"'{key}' 파일 형식이 손상되었습니다(변조 의심): {ex.Message}", ex);
		}
		if (document.KmsEntries.Count < 2)
		{
			throw new CliException(
				ExitCode.IntegrityFailure,
				$"'{key}' 트레일러에 KMS 엔트리가 2개 미만입니다(손상/변조 의심).");
		}

		Dictionary<string, string> values;
		try
		{
			values = await SopsEnvelopeCodec.DecryptAsAppAsync(content, kms, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is CryptographicMacException or AmazonKeyManagementServiceException)
		{
			// MAC 불일치, CMK 접근 불가/키 회수 등 KMS 쪽 실패를 exit 4로 묶는다 -
			// GetObjectAsync는 성공했으니 exit 2가 아니다. InvalidOperationException은
			// 여기서 더 이상 잡지 않는다 - 위에서 트레일러 모양을 이미 보장했으므로, 그런데도
			// 발생한다면 우리 코드의 실제 버그이지 "변조"가 아니다(처리되지 않은 예외로
			// 정직하게 실패해야 한다).
			throw new CliException(
				ExitCode.IntegrityFailure,
				$"'{key}' 복호화/무결성 검증에 실패했습니다(MAC 불일치 또는 CMK 접근 불가): {ex.Message}",
				ex);
		}

		return (values, true);
	}

	private static readonly IReadOnlyDictionary<string, string> EmptyValues =
		new Dictionary<string, string>(StringComparer.Ordinal);
}

public sealed record BundleFetchResult(
	IReadOnlyDictionary<string, string> Values,
	bool BaseBundleFound,
	bool OverwriteBundleFound,
	string? Warning);

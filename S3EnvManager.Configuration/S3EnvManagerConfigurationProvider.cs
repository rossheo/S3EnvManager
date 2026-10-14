using System.Net;
using Amazon;
using Amazon.KeyManagementService;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using S3EnvManager.Sops;

namespace S3EnvManager.Configuration;

/// <summary>Application 측 IConfiguration Provider. 시크릿 번들을 폴링 감시하며 app-facing
/// CMK로만 복호화한다(admin/primary 엔트리는 이 Provider가 전혀 모른다).</summary>
public sealed class S3EnvManagerConfigurationProvider : ConfigurationProvider, IDisposable
{
	private readonly S3EnvManagerConfigurationOptions _options;
	private readonly IAmazonS3 _s3;
	private readonly IAmazonKeyManagementService _kmsClient;
	private readonly IKmsKeyOperations _kms;
	private readonly string _objectKey;
	private readonly string _localCachePath;

	private string? _lastETag;
	// 오브젝트 부재를 이미 인지한 상태 - 아직 안 만든 overwrite 번들처럼 정상적인 부재를
	// 폴링 주기마다 경고하지 않기 위해 둔다. 원격 로드에 성공하면 다시 false.
	private bool _missingAcknowledged;
	private CancellationTokenSource? _pollingCts;
	private Task? _pollingTask;

	public S3EnvManagerConfigurationProvider(S3EnvManagerConfigurationOptions options)
	{
		_options = options;
		// Application 쪽 명명 규칙은 S3EnvManager.Web의 SecretBundleService.ObjectLocation과
		// 독립적으로 구현되어 있으므로, 접미사를 바꾸면 그쪽도 함께 바꿔야 한다.
		var suffix = options.IsOverwriteBundle ? ".overwrite.env" : ".env";
		_objectKey = $"{options.AppName}/{options.EnvSegment}{suffix}";
		_localCachePath = options.LocalCacheFilePath
			?? Path.Combine(Path.GetTempPath(), "s3envmanager-cache",
				$"{options.AppName}_{options.EnvSegment}{(options.IsOverwriteBundle ? "_overwrite" : string.Empty)}.enc");

		var s3Config = new AmazonS3Config();
		if (options.Region is not null)
		{
			// RegionEndpoint는 정상적인 AWS 엔드포인트 해석에, AuthenticationRegion은 SigV4
			// 서명에 쓰인다.
			s3Config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
			s3Config.AuthenticationRegion = options.Region;
		}
		_s3 = options.Credentials is not null
			? new AmazonS3Client(options.Credentials, s3Config)
			: new AmazonS3Client(s3Config);

		var kmsConfig = new AmazonKeyManagementServiceConfig();
		if (options.Region is not null)
		{
			kmsConfig.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
			kmsConfig.AuthenticationRegion = options.Region;
		}
		_kmsClient = options.Credentials is not null
			? new AmazonKeyManagementServiceClient(options.Credentials, kmsConfig)
			: new AmazonKeyManagementServiceClient(kmsConfig);
		_kms = new AwsKmsKeyOperations(_kmsClient);
	}

	/// <summary>ConfigurationProvider 계약상 Load()는 동기여야 해서 GetAwaiter().GetResult()로
	/// 블로킹한다 - 호스트 기동 시점엔 SynchronizationContext가 없어 데드락 위험은 없다.</summary>
	public override void Load()
	{
		try
		{
			LoadFromRemoteAsync(CancellationToken.None).GetAwaiter().GetResult();
		}
		catch (Exception ex) when (IsForbidden(ex))
		{
			ReportDiagnostic(S3EnvManagerLogLevel.Error,
				$"'{_objectKey}' 접근이 거부되었습니다(403) - 자격증명이 폐기/로테이션되었을 수 있습니다. 재발급이 필요합니다.", ex);
			if (!TryLoadFromLocalCache())
			{
				throw;
			}
		}
		catch (Exception ex)
		{
			ReportDiagnostic(S3EnvManagerLogLevel.Warning,
				$"'{_objectKey}' 최초 로드에 실패했습니다. 로컬 캐시로 폴백을 시도합니다.", ex);
			if (!TryLoadFromLocalCache() && !_options.OptionalIfMissing)
			{
				throw;
			}
		}

		StartPolling();
	}

	private async Task LoadFromRemoteAsync(CancellationToken cancellationToken)
	{
		GetObjectResponse response;
		try
		{
			response = await _s3.GetObjectAsync(_options.Bucket, _objectKey, cancellationToken).ConfigureAwait(false);
		}
		catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
		{
			if (!_options.OptionalIfMissing)
			{
				throw;
			}
			SetData(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
			_lastETag = null;
			_missingAcknowledged = true;
			return;
		}

		using (response)
		{
			using var reader = new StreamReader(response.ResponseStream);
			var encryptedContent = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

			await ApplyEncryptedContentAsync(encryptedContent, cancellationToken).ConfigureAwait(false);
			_lastETag = response.ETag?.Trim('"');
			_missingAcknowledged = false;
			await WriteLocalCacheAsync(encryptedContent, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ApplyEncryptedContentAsync(string encryptedContent, CancellationToken cancellationToken)
	{
		var values = await SopsEnvelopeCodec.DecryptAsAppAsync(encryptedContent, _kms, cancellationToken)
			.ConfigureAwait(false);

		var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in values)
		{
			data[NormalizeKey(key)] = value;
		}
		SetData(data);
	}

	private void SetData(Dictionary<string, string?> data)
	{
		Data = data;
	}

	/// <summary>`AddEnvironmentVariables()`와 동일한 규칙: `App__Setting` → `App:Setting`.</summary>
	private static string NormalizeKey(string key) => key.Replace("__", ConfigurationPath.KeyDelimiter);

	private void StartPolling()
	{
		_pollingCts = new CancellationTokenSource();
		_pollingTask = Task.Run(() => PollLoopAsync(_pollingCts.Token));
	}

	private async Task PollLoopAsync(CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(_options.PollInterval, cancellationToken).ConfigureAwait(false);
			}
			catch (TaskCanceledException)
			{
				return;
			}

			try
			{
				var metadata = await _s3.GetObjectMetadataAsync(_options.Bucket, _objectKey, cancellationToken)
					.ConfigureAwait(false);
				var etag = metadata.ETag?.Trim('"');
				if (etag == _lastETag)
				{
					continue;
				}

				await LoadFromRemoteAsync(cancellationToken).ConfigureAwait(false);
				OnReload();
			}
			catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
			{
				// 처음부터 없던 오브젝트(예: 안 만든 overwrite 번들)는 정상 상태라 조용히 넘긴다.
				// 폴링 중 사라진 경우(드묾)만 한 번 경고하고 마지막 값을 유지한다 - 404 스택
				// 트레이스는 정보가 없어 싣지 않는다.
				if (!_missingAcknowledged)
				{
					_missingAcknowledged = true;
					ReportDiagnostic(S3EnvManagerLogLevel.Warning,
						$"'{_objectKey}'를 찾을 수 없습니다. 마지막으로 성공한 값을 유지합니다.", null);
				}
			}
			catch (Exception ex) when (IsForbidden(ex))
			{
				ReportDiagnostic(S3EnvManagerLogLevel.Error,
					$"'{_objectKey}' 접근이 거부되었습니다(403) - 자격증명이 폐기/로테이션되었을 수 있습니다. 재발급이 필요합니다.", ex);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			catch (Exception ex)
			{
				ReportDiagnostic(S3EnvManagerLogLevel.Warning,
					$"'{_objectKey}' 폴링 중 일시적 오류가 발생했습니다. 마지막으로 성공한 값을 유지합니다.", ex);
			}
		}
	}

	private bool TryLoadFromLocalCache()
	{
		try
		{
			if (!File.Exists(_localCachePath))
			{
				return false;
			}

			var encryptedContent = File.ReadAllText(_localCachePath);
			ApplyEncryptedContentAsync(encryptedContent, CancellationToken.None).GetAwaiter().GetResult();
			ReportDiagnostic(S3EnvManagerLogLevel.Warning, $"로컬 캐시('{_localCachePath}')로 폴백했습니다.", null);
			return true;
		}
		catch (Exception ex)
		{
			ReportDiagnostic(S3EnvManagerLogLevel.Error, "로컬 캐시 폴백에도 실패했습니다.", ex);
			return false;
		}
	}

	private async Task WriteLocalCacheAsync(string encryptedContent, CancellationToken cancellationToken)
	{
		try
		{
			var directory = Path.GetDirectoryName(_localCachePath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}
			// 평문이 아니라 sops로 암호화된 원본 그대로 캐싱한다 - 디스크 탈취에도 안전해야
			// 한다는 전제를 이 캐시 파일에도 유지한다.
			await File.WriteAllTextAsync(_localCachePath, encryptedContent, cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			ReportDiagnostic(S3EnvManagerLogLevel.Warning, "로컬 캐시 쓰기에 실패했습니다(다음 성공 시 재시도).", ex);
		}
	}

	private static bool IsForbidden(Exception ex) =>
		ex is AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden };

	private void ReportDiagnostic(S3EnvManagerLogLevel level, string message, Exception? exception) =>
		_options.OnDiagnostic?.Invoke(level, message, exception);

	public void Dispose()
	{
		_pollingCts?.Cancel();
		_pollingCts?.Dispose();
		_s3.Dispose();
		_kmsClient.Dispose();
	}
}
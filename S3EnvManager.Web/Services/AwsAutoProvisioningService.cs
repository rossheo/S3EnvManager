using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.Extensions.DependencyInjection;
using S3EnvManager.Database.Models;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

public sealed class AwsAutoProvisioningService(
	IAmazonSecurityTokenService sts,
	IKmsKeyAdministration kmsAdmin,
	IBootstrapAppIdentityProvisioner appIdentity,
	ICmkRegistryService cmkRegistry,
	IAwsBootstrapCredentialStore credentialStore,
	[FromKeyedServices(CmkRole.App)] IRuntimeAwsCredentialsOverride appCredentialOverride,
	IRuntimeAwsCredentialsOverride adminCredentialOverride,
	IPrimaryStorageSettingsStore primaryStorageSettingsStore,
	IRuntimePrimaryStorageOverride primaryStorageOverride,
	IBucketSelfHealService bucketSelfHeal,
	IBucketHealthStatusStore bucketHealthStatusStore,
	IAuditLogger auditLogger) : IAwsAutoProvisioningService
{
	private const string PrimaryAlias = $"{KmsAliasConventions.ManagedAliasPrefix}-primary";
	private const string AppFacingAlias = $"{KmsAliasConventions.ManagedAliasPrefix}-app";

	public async Task<ProvisioningReport> EnsureProvisionedAsync(
		ProvisioningRequest request, string? actorUserId = null, bool includeBucketProvisioning = true,
		CancellationToken cancellationToken = default)
	{
		var steps = new List<ProvisioningStep>();
		var failed = false;

		async Task RunStepAsync(string name, Func<Task<(ProvisioningStepStatus Status, string Detail)>> action)
		{
			if (failed)
			{
				steps.Add(new ProvisioningStep(name, ProvisioningStepStatus.SkippedDueToEarlierFailure, "이전 단계 실패로 건너뜀"));
				return;
			}
			try
			{
				var (status, detail) = await action().ConfigureAwait(false);
				steps.Add(new ProvisioningStep(name, status, detail));
				failed = status == ProvisioningStepStatus.Failed;
			}
			catch (Exception ex)
			{
				steps.Add(new ProvisioningStep(name, ProvisioningStepStatus.Failed, ex.Message));
				failed = true;
			}
		}

		string accountId = "";
		string adminUserArn = "";
		string appUserArn = "";
		string primaryKeyArn = "";
		string appKeyArn = "";

		await RunStepAsync("자격증명 확인(STS)", async () =>
		{
			var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken)
				.ConfigureAwait(false);
			accountId = identity.Account;
			adminUserArn = identity.Arn;
			return (ProvisioningStepStatus.Done, $"계정 {accountId}, {adminUserArn}");
		}).ConfigureAwait(false);

		await RunStepAsync("부트스트랩 app IAM 사용자 확인/생성", async () =>
		{
			appUserArn = await appIdentity.EnsureUserAsync(cancellationToken).ConfigureAwait(false);
			return (ProvisioningStepStatus.Done, appUserArn);
		}).ConfigureAwait(false);

		await RunStepAsync("primary(admin) CMK 확인/생성", async () =>
		{
			var (arn, status, detail) = await EnsureCmkAsync(
				CmkRole.Admin, PrimaryAlias, "S3EnvManager primary(admin) CMK", cancellationToken).ConfigureAwait(false);
			primaryKeyArn = arn;
			return (status, detail);
		}).ConfigureAwait(false);

		await RunStepAsync("app-facing CMK 확인/생성", async () =>
		{
			var (arn, status, detail) = await EnsureCmkAsync(
				CmkRole.App, AppFacingAlias, "S3EnvManager app-facing CMK", cancellationToken).ConfigureAwait(false);
			appKeyArn = arn;
			return (status, detail);
		}).ConfigureAwait(false);

		await RunStepAsync("키 정책 적용", async () =>
		{
			await PutKeyPolicyWithRetryAsync(
				primaryKeyArn, KmsKeyPolicyDocument.BuildPrimaryKeyPolicyJson(accountId, adminUserArn),
				cancellationToken)
				.ConfigureAwait(false);
			await PutKeyPolicyWithRetryAsync(
				appKeyArn,
				KmsKeyPolicyDocument.BuildAppFacingKeyPolicyJson(accountId, adminUserArn, appUserArn),
				cancellationToken)
				.ConfigureAwait(false);
			return (ProvisioningStepStatus.Done, "primary/app-facing 키 정책 적용 완료");
		}).ConfigureAwait(false);

		await RunStepAsync("CMK 레지스트리 등록", async () =>
		{
			var registrations = await cmkRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
			var registeredNew = new List<string>();
			if (!registrations.Any(r => r.Arn == primaryKeyArn))
			{
				await cmkRegistry.RegisterAsync(CmkRole.Admin, primaryKeyArn, actorUserId, cancellationToken)
					.ConfigureAwait(false);
				registeredNew.Add("admin");
			}
			if (!registrations.Any(r => r.Arn == appKeyArn))
			{
				await cmkRegistry.RegisterAsync(CmkRole.App, appKeyArn, actorUserId, cancellationToken)
					.ConfigureAwait(false);
				registeredNew.Add("app");
			}
			return registeredNew.Count == 0
				? (ProvisioningStepStatus.AlreadyProvisioned, "이미 둘 다 등록됨")
				: (ProvisioningStepStatus.Done, $"새로 등록: {string.Join(", ", registeredNew)}");
		}).ConfigureAwait(false);

		await RunStepAsync("부트스트랩 app 정책 적용", async () =>
		{
			var appCmkArns = (await cmkRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
				.Where(r => r.Role == CmkRole.App).Select(r => r.Arn).ToList();
			await appIdentity.PutPolicyAsync(appCmkArns, cancellationToken).ConfigureAwait(false);
			return (ProvisioningStepStatus.Done, $"등록된 app role CMK {appCmkArns.Count}개로 적용");
		}).ConfigureAwait(false);

		await RunStepAsync("부트스트랩 app Access Key 확인/발급", async () =>
		{
			var existing = await credentialStore.GetAsync(CmkRole.App, cancellationToken).ConfigureAwait(false);
			if (existing is not null)
			{
				return (ProvisioningStepStatus.AlreadyProvisioned, "이미 저장된 자격증명이 있음");
			}

			ProvisionedCredential issued;
			try
			{
				issued = await appIdentity.IssueAccessKeyAsync(cancellationToken).ConfigureAwait(false);
			}
			catch (Amazon.IdentityManagement.Model.LimitExceededException)
			{
				var existingKeyIds = await appIdentity.ListAccessKeyIdsAsync(cancellationToken).ConfigureAwait(false);
				return (ProvisioningStepStatus.Failed,
					$"Access Key가 이미 {existingKeyIds.Count}개(AWS 최대 2개) 있어 새로 발급할 수 없습니다 - " +
					$"저장소 밖에서 발급된 키({string.Join(", ", existingKeyIds)})가 있는지 확인하고 정리한 뒤 다시 시도하세요.");
			}

			await credentialStore.SaveAsync(
				CmkRole.App, issued.AccessKeyId, issued.SecretAccessKey, cancellationToken).ConfigureAwait(false);
			appCredentialOverride.Set(issued.AccessKeyId, issued.SecretAccessKey);
			return (ProvisioningStepStatus.Done, issued.AccessKeyId);
		}).ConfigureAwait(false);

		if (includeBucketProvisioning)
		{
			await RunStepAsync("S3 버킷 확인/생성 및 하드닝", async () =>
			{
				using var s3Client = StorageEndpointClientFactory.BuildClient(
					new StorageEndpointSettings(request.Region), adminCredentialOverride);

				var bucketExisted = await BucketExistsAsync(s3Client, request.Bucket, cancellationToken)
					.ConfigureAwait(false);
				if (!bucketExisted)
				{
					if (!request.CreateBucketIfMissing)
					{
						return (ProvisioningStepStatus.Failed, $"버킷 '{request.Bucket}'이(가) 없습니다 - 자동 생성을 켜거나 먼저 만들어 두세요.");
					}
					await s3Client.PutBucketAsync(
						new PutBucketRequest { BucketName = request.Bucket, UseClientRegion = true }, cancellationToken)
						.ConfigureAwait(false);
				}

				// bucketSelfHeal의 공유 클라이언트는 primaryStorageOverride가 설정돼 있어야 동작한다.
				// 그 값은 원래 다음 단계("주 저장소 설정 저장")에서 채워지므로, 최초 프로비저닝 시
				// 순서상 이 단계가 실패했다 - 인메모리 오버라이드만 앞당겨 설정해 자체 완결시킨다
				// (DB 영속화는 다음 단계가 그대로 담당하므로 여기서 실패해도 재실행이 안전하다).
				primaryStorageOverride.Set(new StorageEndpointSettings(request.Region));
				var healReport = await bucketSelfHeal.HealAsync(request.Bucket, cancellationToken)
					.ConfigureAwait(false);
				bucketHealthStatusStore.Set(healReport);
				return (
					bucketExisted ? ProvisioningStepStatus.AlreadyProvisioned : ProvisioningStepStatus.Done,
					request.Bucket);
			}).ConfigureAwait(false);

			await RunStepAsync("주 저장소 설정 저장", async () =>
			{
				await primaryStorageSettingsStore.SaveAsync(request.Region, request.Bucket, cancellationToken)
					.ConfigureAwait(false);
				primaryStorageOverride.Set(new StorageEndpointSettings(request.Region));
				return (ProvisioningStepStatus.Done, request.Region);
			}).ConfigureAwait(false);
		}

		var report = new ProvisioningReport(steps);
		var details = System.Text.Json.JsonSerializer.Serialize(new
		{
			succeeded = report.Succeeded,
			steps = steps.Select(s => new { s.Name, Status = s.Status.ToString(), s.Detail }),
		}, AuditJsonOptions.Default);
		await auditLogger.LogAsync(
			AuditEventTypes.AutoProvisioningRun, actorUserId, appId: null, details, cancellationToken)
			.ConfigureAwait(false);

		return report;
	}

	// 레지스트리에 이미 등록된 활성 CMK가 있으면 그걸 그대로 쓰고, 없을 때만 alias로 찾거나 새로
	// 만든다 - alias만으로 판단하면 다른 alias로 등록된 기존 CMK를 못 찾아 중복 생성하게 된다.
	private async Task<(string Arn, ProvisioningStepStatus Status, string Detail)> EnsureCmkAsync(
		CmkRole role, string alias, string description, CancellationToken cancellationToken)
	{
		var activeRegistered = (await cmkRegistry.ListAsync(cancellationToken).ConfigureAwait(false))
			.FirstOrDefault(r => r.Role == role && r.Status == CmkStatus.Active);
		if (activeRegistered is not null)
		{
			return (activeRegistered.Arn, ProvisioningStepStatus.AlreadyProvisioned,
				$"CMK 레지스트리에 이미 등록된 활성 {role} CMK를 승계: {activeRegistered.Arn}");
		}

		var existingByAlias = await kmsAdmin.FindKeyArnByAliasAsync(alias, cancellationToken).ConfigureAwait(false);
		if (existingByAlias is not null)
		{
			return (existingByAlias, ProvisioningStepStatus.AlreadyProvisioned, existingByAlias);
		}

		var created = await kmsAdmin.CreateKeyAsync(description, KmsAliasConventions.ManagedTag, cancellationToken)
			.ConfigureAwait(false);
		await kmsAdmin.EnableRotationAsync(created, cancellationToken).ConfigureAwait(false);
		await kmsAdmin.EnsureAliasAsync(alias, created, cancellationToken).ConfigureAwait(false);
		return (created, ProvisioningStepStatus.Done, created);
	}

	private static async Task<bool> BucketExistsAsync(
		IAmazonS3 s3Client, string bucket, CancellationToken cancellationToken)
	{
		try
		{
			await s3Client.GetBucketVersioningAsync(bucket, cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (AmazonS3Exception ex) when (ex.ErrorCode == "NoSuchBucket")
		{
			return false;
		}
	}

	// 방금 만든 IAM 사용자를 Principal로 넣은 키 정책은 IAM 전파 지연으로
	// MalformedPolicyDocumentException이 날 수 있다 - 지수 백오프로 최대 ~30초 재시도한다.
	private async Task PutKeyPolicyWithRetryAsync(
		string keyArn, string policyJson, CancellationToken cancellationToken)
	{
		var delay = TimeSpan.FromSeconds(1);
		for (var attempt = 1; ; attempt++)
		{
			try
			{
				await kmsAdmin.PutKeyPolicyAsync(keyArn, policyJson, cancellationToken).ConfigureAwait(false);
				return;
			}
			catch (Amazon.KeyManagementService.Model.MalformedPolicyDocumentException) when (attempt < 6)
			{
				await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
				delay *= 2;
			}
		}
	}
}
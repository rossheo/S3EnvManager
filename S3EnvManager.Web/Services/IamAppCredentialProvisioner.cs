using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;

namespace S3EnvManager.Web.Services;

public sealed class IamAppCredentialProvisioner(IAmazonIdentityManagementService iam)
	: IAppCredentialProvisioner
{
	public async Task<ProvisionedCredential> IssueAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default)
	{
		var userName = UserName(appName);

		try
		{
			await iam.CreateUserAsync(new CreateUserRequest { UserName = userName }, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (EntityAlreadyExistsException)
		{
			// 이미 발급된 적 있는 앱 - 사용자는 그대로 두고 정책만 최신 상태로 덮어쓴다.
		}

		await iam.PutUserPolicyAsync(new PutUserPolicyRequest
		{
			UserName = userName,
			PolicyName = PolicyName(appName),
			PolicyDocument = BuildPolicyDocument(appName, bucket, appFacingCmkArns),
		}, cancellationToken).ConfigureAwait(false);

		var accessKey = await iam.CreateAccessKeyAsync(
			new CreateAccessKeyRequest { UserName = userName }, cancellationToken)
			.ConfigureAwait(false);

		return new ProvisionedCredential(accessKey.AccessKey.AccessKeyId, accessKey.AccessKey.SecretAccessKey);
	}

	public async Task ReapplyPolicyAsync(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns,
		CancellationToken cancellationToken = default)
	{
		var userName = UserName(appName);
		try
		{
			await iam.PutUserPolicyAsync(new PutUserPolicyRequest
			{
				UserName = userName,
				PolicyName = PolicyName(appName),
				PolicyDocument = BuildPolicyDocument(appName, bucket, appFacingCmkArns),
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (NoSuchEntityException)
		{
			// 이 App은 자격증명을 발급한 적이 없다 - 아직 아무 정책도 없으므로 손댈 게 없다.
		}
	}

	public Task RevokeAccessKeyAsync(
		string appName, string accessKeyId, CancellationToken cancellationToken = default) =>
		iam.DeleteAccessKeyAsync(new DeleteAccessKeyRequest
		{
			UserName = UserName(appName),
			AccessKeyId = accessKeyId,
		}, cancellationToken);

	public async Task DeleteUserAsync(string appName, CancellationToken cancellationToken = default)
	{
		var userName = UserName(appName);

		try
		{
			var accessKeys = await iam.ListAccessKeysAsync(
				new ListAccessKeysRequest { UserName = userName }, cancellationToken)
				.ConfigureAwait(false);
			foreach (var accessKey in accessKeys.AccessKeyMetadata)
			{
				await iam.DeleteAccessKeyAsync(
					new DeleteAccessKeyRequest { UserName = userName, AccessKeyId = accessKey.AccessKeyId },
					cancellationToken)
					.ConfigureAwait(false);
			}

			await iam.DeleteUserPolicyAsync(
				new DeleteUserPolicyRequest { UserName = userName, PolicyName = PolicyName(appName) },
				cancellationToken)
				.ConfigureAwait(false);

			await iam.DeleteUserAsync(new DeleteUserRequest { UserName = userName }, cancellationToken)
				.ConfigureAwait(false);
		}
		catch (NoSuchEntityException)
		{
			// 애초에 자격증명을 발급한 적 없는 App - 지울 게 없으므로 그대로 넘어간다(멱등).
		}
	}

	private static string UserName(string appName) => $"s3envmanager-app-{appName}";

	private static string PolicyName(string appName) => $"s3envmanager-app-{appName}-policy";

	// GetObjectVersion은 부여하지 않아 noncurrent 버전 접근 불가. kms:Decrypt는 app role CMK
	// 전부(활성+보조)에 부여한다 - 옛 시크릿이 승격 전 CMK로 감싸져 있을 수 있어 활성 하나로는 부족.
	private static string BuildPolicyDocument(
		string appName, string bucket, IReadOnlyCollection<string> appFacingCmkArns)
	{
		var kmsResources = string.Join(",\n        ", appFacingCmkArns.Select(arn => $"\"{arn}\""));
		return $$"""
		{
		  "Version": "2012-10-17",
		  "Statement": [
		    {
		      "Sid": "ReadOwnSecretBundles",
		      "Effect": "Allow",
		      "Action": ["s3:GetObject", "s3:HeadObject"],
		      "Resource": "arn:aws:s3:::{{bucket}}/{{appName}}/*"
		    },
		    {
		      "Sid": "DecryptOwnDataKey",
		      "Effect": "Allow",
		      "Action": "kms:Decrypt",
		      "Resource": [
		        {{kmsResources}}
		      ],
		      "Condition": {
		        "StringEquals": { "kms:EncryptionContext:app": "{{appName}}" }
		      }
		    }
		  ]
		}
		""";
	}
}
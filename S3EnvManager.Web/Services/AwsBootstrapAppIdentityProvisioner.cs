using Amazon.Extensions.NETCore.Setup;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using S3EnvManager.Sops;

namespace S3EnvManager.Web.Services;

// 승계(adopt) 지원 - 저장된 app 자격증명이 있으면 그 키로 sts:GetCallerIdentity를 호출해(어떤
// IAM 정책으로도 거부될 수 없어 별도 권한 불필요) 실제 IAM 사용자명을 알아내고 그 이름을 그대로
// 쓴다 - DefaultUserName이 아니어도 새 identity를 만들지 않는다.
public sealed class AwsBootstrapAppIdentityProvisioner(
	IAmazonIdentityManagementService iam, IAwsBootstrapCredentialStore credentialStore,
	IConfiguration configuration)
	: IBootstrapAppIdentityProvisioner
{
	public const string DefaultUserName = "s3envmanager-app";
	private const string PolicyName = "s3envmanager-app-policy";
	private string? _resolvedUserName;

	public async Task<string> EnsureUserAsync(CancellationToken cancellationToken = default)
	{
		var userName = await ResolveUserNameAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var created = await iam.CreateUserAsync(new CreateUserRequest { UserName = userName }, cancellationToken)
				.ConfigureAwait(false);
			return created.User.Arn;
		}
		catch (EntityAlreadyExistsException)
		{
			var existing = await iam.GetUserAsync(new GetUserRequest { UserName = userName }, cancellationToken)
				.ConfigureAwait(false);
			return existing.User.Arn;
		}
	}

	public async Task PutPolicyAsync(
		IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default)
	{
		var userName = await ResolveUserNameAsync(cancellationToken).ConfigureAwait(false);
		await iam.PutUserPolicyAsync(new PutUserPolicyRequest
		{
			UserName = userName,
			PolicyName = PolicyName,
			PolicyDocument = BootstrapPolicyDocument.BuildAppPolicyJson(appCmkArns),
		}, cancellationToken).ConfigureAwait(false);
	}

	public async Task<bool> TryPutPolicyIfProvisionedAsync(
		IReadOnlyCollection<string> appCmkArns, CancellationToken cancellationToken = default)
	{
		try
		{
			await PutPolicyAsync(appCmkArns, cancellationToken).ConfigureAwait(false);
			return true;
		}
		catch (NoSuchEntityException)
		{
			// 자동 프로비저닝을 아직 실행한 적 없어 app IAM 사용자 자체가 없다 - 갱신할 정책이
			// 없으므로 조용히 넘어간다(IamAppCredentialProvisioner의 앱별 ReapplyPolicyAsync와
			// 동일한 멱등 규칙).
			return false;
		}
	}

	public async Task<ProvisionedCredential> IssueAccessKeyAsync(CancellationToken cancellationToken = default)
	{
		var userName = await ResolveUserNameAsync(cancellationToken).ConfigureAwait(false);
		var response = await iam.CreateAccessKeyAsync(
			new CreateAccessKeyRequest { UserName = userName }, cancellationToken).ConfigureAwait(false);
		return new ProvisionedCredential(response.AccessKey.AccessKeyId, response.AccessKey.SecretAccessKey);
	}

	public async Task<IReadOnlyList<string>> ListAccessKeyIdsAsync(CancellationToken cancellationToken = default)
	{
		var userName = await ResolveUserNameAsync(cancellationToken).ConfigureAwait(false);
		var response = await iam.ListAccessKeysAsync(
			new ListAccessKeysRequest { UserName = userName }, cancellationToken).ConfigureAwait(false);
		return response.AccessKeyMetadata.Select(k => k.AccessKeyId).ToList();
	}

	public async Task DeleteAccessKeyAsync(string accessKeyId, CancellationToken cancellationToken = default)
	{
		var userName = await ResolveUserNameAsync(cancellationToken).ConfigureAwait(false);
		await iam.DeleteAccessKeyAsync(
			new DeleteAccessKeyRequest { UserName = userName, AccessKeyId = accessKeyId }, cancellationToken)
			.ConfigureAwait(false);
	}

	private async Task<string> ResolveUserNameAsync(CancellationToken cancellationToken)
	{
		if (_resolvedUserName is not null)
		{
			return _resolvedUserName;
		}

		_resolvedUserName = await TryDetectAdoptedUserNameAsync(cancellationToken).ConfigureAwait(false)
			?? DefaultUserName;
		return _resolvedUserName;
	}

	private async Task<string?> TryDetectAdoptedUserNameAsync(CancellationToken cancellationToken)
	{
		var storedAppCredential = await credentialStore.GetAsync(CmkRole.App, cancellationToken)
			.ConfigureAwait(false);
		if (storedAppCredential is null)
		{
			return null;
		}

		try
		{
			var awsOptions = configuration.GetAWSOptions();
			awsOptions.Credentials = new BasicAWSCredentials(
				storedAppCredential.Value.AccessKeyId, storedAppCredential.Value.SecretAccessKey);
			using var sts = awsOptions.CreateServiceClient<IAmazonSecurityTokenService>();
			var identity = await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest(), cancellationToken)
				.ConfigureAwait(false);
			// arn:aws:iam::<account>:user/<username>
			var userName = identity.Arn.Split('/').LastOrDefault();
			return string.IsNullOrWhiteSpace(userName) ? null : userName;
		}
		catch
		{
			// 저장된 자격증명이 무효하거나(예: AWS 콘솔에서 직접 폐기됨) STS 호출 자체가
			// 실패하면 기본 이름으로 폴백한다 - EnsureUserAsync가 그 이름으로 사용자 존재
			// 여부를 다시 판단한다.
			return null;
		}
	}
}
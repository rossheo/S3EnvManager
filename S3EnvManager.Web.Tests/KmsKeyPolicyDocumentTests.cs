using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class KmsKeyPolicyDocumentTests
{
	private const string AccountId = "123456789012";
	private const string AdminUserArn = "arn:aws:iam::123456789012:user/s3envmanager-admin";
	private const string AppUserArn = "arn:aws:iam::123456789012:user/s3envmanager-app";

	[Fact]
	public void BuildPrimaryKeyPolicyJson_GrantsRootDelegationAndAdminEnvelopeAccess()
	{
		var json = KmsKeyPolicyDocument.BuildPrimaryKeyPolicyJson(AccountId, AdminUserArn);

		Assert.Contains("arn:aws:iam::123456789012:root", json);
		Assert.Contains(AdminUserArn, json);
		Assert.Contains("kms:GenerateDataKey", json);
		Assert.Contains("kms:Encrypt", json);
		Assert.Contains("kms:Decrypt", json);
	}

	[Fact]
	public void BuildPrimaryKeyPolicyJson_DoesNotMentionAppIdentity()
	{
		var json = KmsKeyPolicyDocument.BuildPrimaryKeyPolicyJson(AccountId, AdminUserArn);

		Assert.DoesNotContain(AppUserArn, json);
	}

	[Fact]
	public void BuildAppFacingKeyPolicyJson_GrantsAdminRewrapAndAppWrapOnly()
	{
		var json = KmsKeyPolicyDocument.BuildAppFacingKeyPolicyJson(AccountId, AdminUserArn, AppUserArn);

		Assert.Contains("arn:aws:iam::123456789012:root", json);
		Assert.Contains(AdminUserArn, json);
		Assert.Contains(AppUserArn, json);
		Assert.Contains("kms:Decrypt", json);
		Assert.Contains("kms:Encrypt", json);
	}
}
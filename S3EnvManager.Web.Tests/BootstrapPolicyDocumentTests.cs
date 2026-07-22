using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class BootstrapPolicyDocumentTests
{
	[Fact]
	public void BuildAdminPolicyJson_ScopesKmsActionsByManagedTag_NotByArn()
	{
		var json = BootstrapPolicyDocument.BuildAdminPolicyJson("my-bucket-*");

		Assert.Contains("aws:ResourceTag/s3envmanager-managed", json);
		Assert.Contains("aws:RequestTag/s3envmanager-managed", json);
		Assert.Contains("kms:CreateKey", json);
		Assert.Contains("kms:PutKeyPolicy", json);
		Assert.DoesNotContain("REGION", json);
		Assert.DoesNotContain("ACCOUNT", json);
	}

	[Fact]
	public void BuildAdminPolicyJson_ScopesIamProvisioningToBootstrapAndPerAppUsers()
	{
		var json = BootstrapPolicyDocument.BuildAdminPolicyJson("my-bucket-*");

		Assert.Contains("arn:aws:iam::*:user/s3envmanager-app\"", json);
		Assert.Contains("arn:aws:iam::*:user/s3envmanager-app-*", json);
	}

	[Fact]
	public void BuildAdminPolicyJson_InterpolatesBucketIntoResourceArns()
	{
		var json = BootstrapPolicyDocument.BuildAdminPolicyJson("my-bucket-*");

		Assert.Contains("arn:aws:s3:::my-bucket-*/*", json);
		Assert.Contains("arn:aws:s3:::my-bucket-*\"", json);
	}

	// s3:ListBucket 없으면 없는 키의 GetObject가 404 대신 403이 되어(AWS 의도적 동작)
	// GetCurrentAsync의 "새 시크릿" 판별(404)이 깨진다.
	[Fact]
	public void BuildAdminPolicyJson_GrantsBucketAndVersionListingPermissions()
	{
		var json = BootstrapPolicyDocument.BuildAdminPolicyJson("my-bucket-*");

		Assert.Contains("s3:ListBucket\"", json);
		Assert.Contains("s3:ListBucketVersions", json);
	}

	// S3는 버전 지정 GetObject/DeleteObject를 별도 IAM 액션으로 취급한다 - 빠지면 실 AWS에서만
	// AccessDenied(LocalStack은 IAM 미강제라 드러나지 않았다).
	[Fact]
	public void BuildAdminPolicyJson_GrantsVersionedObjectAccess()
	{
		var json = BootstrapPolicyDocument.BuildAdminPolicyJson("my-bucket-*");

		Assert.Contains("s3:GetObjectVersion", json);
		Assert.Contains("s3:DeleteObjectVersion", json);
	}

	[Fact]
	public void BuildAppPolicyJson_ListsAllProvidedCmkArnsAsResources()
	{
		var json = BootstrapPolicyDocument.BuildAppPolicyJson([
			"arn:aws:kms:ap-northeast-2:123456789012:key/active-cmk",
			"arn:aws:kms:ap-northeast-2:123456789012:key/secondary-cmk",
		]);

		Assert.Contains("arn:aws:kms:ap-northeast-2:123456789012:key/active-cmk", json);
		Assert.Contains("arn:aws:kms:ap-northeast-2:123456789012:key/secondary-cmk", json);
		Assert.Contains("kms:Encrypt", json);
		Assert.DoesNotContain("kms:Decrypt", json);
	}
}
namespace S3EnvManager.Web.Services;

// 코드가 실제로 쓰는 액션 목록으로부터 IAM 정책을 생성한다 - 화면 문서와 실제 권한의 드리프트를 막는 단일 소스.
// admin 정책은 CMK ARN을 파라미터로 받지 않는다 - 프로비저닝 시점엔 CMK가 아직 없으므로,
// "s3envmanager-managed" 태그가 붙은 키로만 KMS 권한을 스코프한다.
public static class BootstrapPolicyDocument
{
	public static string BuildAdminPolicyJson(string bucket) =>
		$$"""
		{
		  "Version": "2012-10-17",
		  "Statement": [
		    {
		      "Sid": "SecretBundleReadWrite",
		      "Effect": "Allow",
		      "Action": [
		        "s3:GetObject",
		        "s3:GetObjectVersion",
		        "s3:PutObject",
		        "s3:DeleteObject",
		        "s3:DeleteObjectVersion"
		      ],
		      "Resource": "arn:aws:s3:::{{bucket}}/*"
		    },
		    {
		      "Sid": "BucketManagement",
		      "Effect": "Allow",
		      "Action": [
		        "s3:CreateBucket",
		        "s3:ListBucket",
		        "s3:ListBucketVersions",
		        "s3:GetBucketVersioning",
		        "s3:PutBucketVersioning",
		        "s3:GetBucketPublicAccessBlock",
		        "s3:PutBucketPublicAccessBlock",
		        "s3:GetBucketOwnershipControls",
		        "s3:PutBucketOwnershipControls",
		        "s3:GetLifecycleConfiguration",
		        "s3:PutLifecycleConfiguration",
		        "s3:GetBucketPolicy"
		      ],
		      "Resource": "arn:aws:s3:::{{bucket}}"
		    },
		    {
		      "Sid": "KmsUseManagedKeys",
		      "Effect": "Allow",
		      "Action": [
		        "kms:GenerateDataKey",
		        "kms:Encrypt",
		        "kms:Decrypt",
		        "kms:DescribeKey"
		      ],
		      "Resource": "*",
		      "Condition": {
		        "StringEquals": { "aws:ResourceTag/s3envmanager-managed": "true" }
		      }
		    },
		    {
		      "Sid": "KmsCreateManagedKeys",
		      "Effect": "Allow",
		      "Action": "kms:CreateKey",
		      "Resource": "*",
		      "Condition": {
		        "StringEquals": { "aws:RequestTag/s3envmanager-managed": "true" }
		      }
		    },
		    {
		      "Sid": "KmsTagManagedKeys",
		      "Effect": "Allow",
		      "Action": "kms:TagResource",
		      "Resource": "*",
		      "Condition": {
		        "StringEquals": { "aws:RequestTag/s3envmanager-managed": "true" }
		      }
		    },
		    {
		      "Sid": "KmsAdministerManagedKeys",
		      "Effect": "Allow",
		      "Action": [
		        "kms:PutKeyPolicy",
		        "kms:GetKeyPolicy",
		        "kms:EnableKeyRotation"
		      ],
		      "Resource": "*",
		      "Condition": {
		        "StringEquals": { "aws:ResourceTag/s3envmanager-managed": "true" }
		      }
		    },
		    {
		      "Sid": "KmsManageAliases",
		      "Effect": "Allow",
		      "Action": [
		        "kms:CreateAlias",
		        "kms:UpdateAlias",
		        "kms:ListAliases"
		      ],
		      "Resource": "*"
		    },
		    {
		      "Sid": "AppIdentityProvisioning",
		      "Effect": "Allow",
		      "Action": [
		        "iam:CreateUser",
		        "iam:GetUser",
		        "iam:PutUserPolicy",
		        "iam:DeleteUserPolicy",
		        "iam:CreateAccessKey",
		        "iam:ListAccessKeys",
		        "iam:DeleteAccessKey",
		        "iam:DeleteUser"
		      ],
		      "Resource": [
		        "arn:aws:iam::*:user/s3envmanager-app",
		        "arn:aws:iam::*:user/s3envmanager-app-*"
		      ]
		    }
		  ]
		}
		""";

	// kms:Encrypt만 부여하고 kms:Decrypt는 부여하지 않는다 - 유출되어도 아무것도 복호화할 수 없다.
	public static string BuildAppPolicyJson(IReadOnlyCollection<string> appCmkArns)
	{
		var resources = string.Join(",\n        ", appCmkArns.Select(arn => $"\"{arn}\""));
		return $$"""
		{
		  "Version": "2012-10-17",
		  "Statement": [
		    {
		      "Sid": "AppFacingCmkWrapOnly",
		      "Effect": "Allow",
		      "Action": "kms:Encrypt",
		      "Resource": [
		        {{resources}}
		      ]
		    }
		  ]
		}
		""";
	}
}
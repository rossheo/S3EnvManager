namespace S3EnvManager.Web.Services;

// root-delegation 문(필수, IAM 정책이 이 키에도 평가되게 함) + 명시적 pinning 문(defense-in-depth)
// 두 개로 구성한다. 앱별 kms:Decrypt는 IamAppCredentialProvisioner가 앱별 IAM 정책에
// EncryptionContext:app 조건으로 이미 부여하므로 여기엔 앱별 문을 넣지 않는다 - 앱 생성/삭제마다
// 32KB 쿼터의 키 정책을 다시 쓸 필요가 없어진다.
public static class KmsKeyPolicyDocument
{
	// Application/app identity는 primary CMK에 대해 어떤 권한도 갖지 않는다(primary/app-facing 분리의 핵심).
	public static string BuildPrimaryKeyPolicyJson(string accountId, string adminUserArn) =>
		$$"""
		{
		  "Version": "2012-10-17",
		  "Statement": [
		    {
		      "Sid": "EnableIamUserPermissions",
		      "Effect": "Allow",
		      "Principal": { "AWS": "arn:aws:iam::{{accountId}}:root" },
		      "Action": "kms:*",
		      "Resource": "*"
		    },
		    {
		      "Sid": "AllowAdminEnvelopeAccess",
		      "Effect": "Allow",
		      "Principal": { "AWS": "{{adminUserArn}}" },
		      "Action": [
		        "kms:GenerateDataKey",
		        "kms:Encrypt",
		        "kms:Decrypt"
		      ],
		      "Resource": "*"
		    }
		  ]
		}
		""";

	// 앱별 IAM 사용자의 Decrypt는 이 키 정책이 아니라 root-delegation을 통해 앱별 IAM 정책으로 성립한다.
	public static string BuildAppFacingKeyPolicyJson(string accountId, string adminUserArn, string appUserArn) =>
		$$"""
		{
		  "Version": "2012-10-17",
		  "Statement": [
		    {
		      "Sid": "EnableIamUserPermissions",
		      "Effect": "Allow",
		      "Principal": { "AWS": "arn:aws:iam::{{accountId}}:root" },
		      "Action": "kms:*",
		      "Resource": "*"
		    },
		    {
		      "Sid": "AllowAdminRewrapOnRemoval",
		      "Effect": "Allow",
		      "Principal": { "AWS": "{{adminUserArn}}" },
		      "Action": [
		        "kms:Decrypt",
		        "kms:Encrypt"
		      ],
		      "Resource": "*"
		    },
		    {
		      "Sid": "AllowAppWrapOnly",
		      "Effect": "Allow",
		      "Principal": { "AWS": "{{appUserArn}}" },
		      "Action": "kms:Encrypt",
		      "Resource": "*"
		    }
		  ]
		}
		""";
}
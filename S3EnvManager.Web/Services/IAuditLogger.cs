namespace S3EnvManager.Web.Services;

public interface IAuditLogger
{
	Task LogAsync(
		string eventType, string? actorUserId, Guid? appId, string? details,
		CancellationToken cancellationToken = default);
}

public static class AuditEventTypes
{
	public const string SecretEdited = "SecretEdited";
	public const string OverwriteSecretEdited = "OverwriteSecretEdited";
	public const string CredentialIssued = "CredentialIssued";
	public const string CredentialRevoked = "CredentialRevoked";
	public const string BucketSelfHealed = "BucketSelfHealed";
	public const string CmkRegistered = "CmkRegistered";
	public const string CmkPromoted = "CmkPromoted";
	public const string CmkRemoved = "CmkRemoved";
	public const string DataKeyRotated = "DataKeyRotated";
	public const string DataKeyRotationIntervalChanged = "DataKeyRotationIntervalChanged";
	public const string DbBackupAccountRotated = "DbBackupAccountRotated";
	public const string DbBackupAccountPasswordRevealed = "DbBackupAccountPasswordRevealed";
	public const string FeatureSwitchChanged = "FeatureSwitchChanged";
	public const string AutoProvisioningRun = "AutoProvisioningRun";
	public const string DataProtectionCertificateRotated = "DataProtectionCertificateRotated";
	public const string SharedSecretCreated = "SharedSecretCreated";
	public const string SharedSecretUpdated = "SharedSecretUpdated";
	public const string SharedSecretDeleted = "SharedSecretDeleted";
	public const string SharedSecretGrantAdded = "SharedSecretGrantAdded";
	public const string SharedSecretGrantRevoked = "SharedSecretGrantRevoked";
	public const string SharedSecretReferenceAttached = "SharedSecretReferenceAttached";
	public const string SharedSecretReferenceDetached = "SharedSecretReferenceDetached";
	public const string SharedSecretCascadeMaterialized = "SharedSecretCascadeMaterialized";
}
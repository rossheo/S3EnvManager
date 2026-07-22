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
}
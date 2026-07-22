using System.Net;
using Amazon.Runtime;
using Amazon.Runtime.Credentials.Internal;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;

namespace S3EnvManager.Web.Tests;

/// <summary>sts:GetCallerIdentity만 구현하고 나머지는 NotSupportedException으로 막아둔다.</summary>
public sealed class FakeSecurityTokenService : IAmazonSecurityTokenService
{
	public const string Account = "132156777934";
	public const string AdminUserArn = "arn:aws:iam::132156777934:user/fake-admin";

	public IClientConfig Config => throw new NotSupportedException();

	public Task<GetCallerIdentityResponse> GetCallerIdentityAsync(
		GetCallerIdentityRequest request, CancellationToken cancellationToken = default) =>
		Task.FromResult(new GetCallerIdentityResponse { Account = Account, Arn = AdminUserArn });

	public Task<AssumeRoleResponse> AssumeRoleAsync(
		AssumeRoleRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<AssumeRoleWithSAMLResponse> AssumeRoleWithSAMLAsync(
		AssumeRoleWithSAMLRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<AssumeRoleWithWebIdentityResponse> AssumeRoleWithWebIdentityAsync(
		AssumeRoleWithWebIdentityRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<AssumeRootResponse> AssumeRootAsync(
		AssumeRootRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<DecodeAuthorizationMessageResponse> DecodeAuthorizationMessageAsync(
		DecodeAuthorizationMessageRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetAccessKeyInfoResponse> GetAccessKeyInfoAsync(
		GetAccessKeyInfoRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetDelegatedAccessTokenResponse> GetDelegatedAccessTokenAsync(
		GetDelegatedAccessTokenRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetFederationTokenResponse> GetFederationTokenAsync(
		GetFederationTokenRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetSessionTokenResponse> GetSessionTokenAsync(CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetSessionTokenResponse> GetSessionTokenAsync(
		GetSessionTokenRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Task<GetWebIdentityTokenResponse> GetWebIdentityTokenAsync(
		GetWebIdentityTokenRequest request, CancellationToken cancellationToken = default) =>
		throw new NotSupportedException();

	public Amazon.Runtime.Endpoints.Endpoint DetermineServiceOperationEndpoint(AmazonWebServiceRequest request) =>
		throw new NotSupportedException();

	public AssumeRoleImmutableCredentials CredentialsFromAssumeRoleAuthentication(
		string username, string password, AssumeRoleAWSCredentialsOptions options) =>
		throw new NotSupportedException();

	public Task<AssumeRoleImmutableCredentials> CredentialsFromAssumeRoleAuthenticationAsync(
		string username, string password, AssumeRoleAWSCredentialsOptions options) =>
		throw new NotSupportedException();

	public SAMLImmutableCredentials CredentialsFromSAMLAuthentication(
		string principalArn, string roleArn, string samlAssertion, TimeSpan duration, ICredentials userCredentials) =>
		throw new NotSupportedException();

	public Task<SAMLImmutableCredentials> CredentialsFromSAMLAuthenticationAsync(
		string principalArn, string roleArn, string samlAssertion, TimeSpan duration, ICredentials userCredentials) =>
		throw new NotSupportedException();

	public AssumeRoleImmutableCredentials CredentialsFromAssumeRoleWithWebIdentityAuthentication(
		string roleArn, string roleSessionName, string webIdentityToken,
		AssumeRoleWithWebIdentityCredentialsOptions options) =>
		throw new NotSupportedException();

	public Task<AssumeRoleImmutableCredentials> CredentialsFromAssumeRoleWithWebIdentityAuthenticationAsync(
		string roleArn, string roleSessionName, string webIdentityToken,
		AssumeRoleWithWebIdentityCredentialsOptions options) =>
		throw new NotSupportedException();

	public void Dispose()
	{
	}
}

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public static class DataProtectionCertificateStore
{
	// 로우가 있는데 주어진 비밀번호로 하나도 못 열면 "비밀번호 오설정"이므로 예외를 던진다 -
	// 조용히 새 인증서를 발급하면 기존에 암호화된 값이 영구 미복호화 상태가 된다.
	public static async Task<IReadOnlyList<X509Certificate2>> LoadAllAsync(
		ApplicationDbContext db, string password, CancellationToken cancellationToken = default)
	{
		var rows = await db.DataProtectionCertificates.AsNoTracking()
			.OrderByDescending(c => c.NotBefore)
			.ToListAsync(cancellationToken).ConfigureAwait(false);

		if (rows.Count == 0)
		{
			return [];
		}

		var certificates = new List<X509Certificate2>(rows.Count);
		var failures = new List<Exception>();
		foreach (var row in rows)
		{
			try
			{
				certificates.Add(DataProtectionCertificateFactory.Load(row.Pkcs12, password));
			}
			catch (CryptographicException ex)
			{
				failures.Add(ex);
			}
		}

		if (certificates.Count == 0)
		{
			// 여기서는 일부러 기동을 막는다 - 이 실패는 앱 안에서 고칠 수 없고(비밀번호는 DB 밖에
			// 있다) 그냥 진행하면 위 Count == 0 분기가 새 인증서를 발급해 기존 DataProtection
			// 키가 전부 복호화 불능이 된다. 대신 무엇을 고쳐야 하는지 메시지에 담는다.
			throw new InvalidOperationException(
				$"DataProtectionCertificates에 {rows.Count}개의 인증서 로우가 있지만 설정된 비밀번호로 하나도 열지 못했습니다. " +
				"DataProtectionCertificate:Password(user-secrets의 Parameters:dataprotection-cert-password 또는 " +
				"환경변수 DataProtectionCertificate__Password)가 이 인증서를 만들 때 쓴 값과 같은지 확인하세요. " +
				"비밀번호를 잃어버렸다면 기존 로그인 세션을 버리고 DataProtectionCertificates/DataProtectionKeys를 " +
				"비운 뒤 재기동해야 합니다.",
				new AggregateException(failures));
		}

		return certificates;
	}

	public static async Task<X509Certificate2> IssueAndSaveAsync(
		ApplicationDbContext db, string password, Int32 validityYears, TimeProvider timeProvider,
		CancellationToken cancellationToken = default)
	{
		var (certificate, pkcs12, notBefore, notAfter) = DataProtectionCertificateFactory.CreateSelfSigned(
			validityYears, password, timeProvider);

		db.DataProtectionCertificates.Add(new DataProtectionCertificate
		{
			Id = Guid.NewGuid(),
			Pkcs12 = pkcs12,
			Thumbprint = certificate.Thumbprint,
			NotBefore = notBefore,
			NotAfter = notAfter,
			CreatedAt = timeProvider.GetUtcNow(),
		});
		await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

		return certificate;
	}
}
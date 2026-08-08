using Microsoft.EntityFrameworkCore;
using S3EnvManager.Database;
using S3EnvManager.Database.Models;

namespace S3EnvManager.Web.Services;

public sealed class AppRegistrationService(
	ApplicationDbContext db, IBucketSelfHealService bucketSelfHeal,
	IBucketHealthStatusStore bucketHealthStatusStore, IPrimaryStorageSettingsStore primaryStorageSettingsStore,
	IAuditLogger auditLogger)
	: IAppRegistrationService
{
	private static readonly EnvName[] FixedEnvNames = [EnvName.Dev, EnvName.Staging, EnvName.Product];

	public async Task<App> RegisterAsync(
		string name, string? actorUserId = null, CancellationToken cancellationToken = default)
	{
		var nameError = AppNameValidator.Validate(name);
		if (nameError is not null)
		{
			throw new InvalidAppNameException(nameError);
		}

		var bucket = await primaryStorageSettingsStore.GetLastProvisionedBucketAsync(cancellationToken)
			.ConfigureAwait(false)
			?? throw new InvalidOperationException("주 저장소가 아직 프로비저닝되지 않았습니다.");

		if (await db.Apps.AnyAsync(a => a.Name == name && a.DeletedAt == null, cancellationToken)
			.ConfigureAwait(false))
		{
			throw new AppNameAlreadyExistsException(name);
		}

		var app = new App
		{
			Id = Guid.NewGuid(),
			Name = name,
			CreatedAt = DateTimeOffset.UtcNow,
		};
		foreach (var envName in FixedEnvNames)
		{
			app.Envs.Add(new Env { Id = Guid.NewGuid(), AppId = app.Id, Name = envName });
		}

		// NpgsqlRetryingExecutionStrategy는 수동 트랜잭션을 재시도 단위 밖에서 여는 것을 허용하지
		// 않으므로 시작~커밋 전체를 delegate 안에 넣는다.
		var strategy = db.Database.CreateExecutionStrategy();
		await strategy.ExecuteAsync(async () =>
		{
			db.ChangeTracker.Clear();
			await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken)
				.ConfigureAwait(false);
			db.Apps.Add(app);
			await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

			// 버킷 접근 자체가 실패하면 App 등록 전체를 되돌린다 - 쓸 수 없는 버킷을 가리키는
			// App을 남겨두지 않기 위함이다.
			try
			{
				var report = await bucketSelfHeal.HealAsync(bucket, cancellationToken).ConfigureAwait(false);
				bucketHealthStatusStore.Set(report);
			}
			catch
			{
				await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
				throw;
			}

			// 자가 치유까지 성공한 뒤에만 남긴다 - "등록됨"이 곧 "쓸 수 있는 버킷을 가리킨다"는 뜻이
			// 되도록. auditLogger는 같은 스코프의 db를 쓰므로 이 트랜잭션에 함께 커밋된다.
			var details = System.Text.Json.JsonSerializer.Serialize(
				new { name, bucket }, AuditJsonOptions.Default);
			await auditLogger.LogAsync(
				AuditEventTypes.AppRegistered, actorUserId, app.Id, details, cancellationToken)
				.ConfigureAwait(false);

			await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
		}).ConfigureAwait(false);
		return app;
	}

	public Task<List<App>> ListActiveAsync(CancellationToken cancellationToken = default) =>
		db.Apps.Where(a => a.DeletedAt == null)
			.Include(a => a.Envs)
			.OrderBy(a => a.Name)
			.ToListAsync(cancellationToken);
}
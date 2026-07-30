namespace S3EnvManager.Cli;

/// <summary>S3 GetObject를 감싸는 얇은 추상화. BundleFetcher가 IAmazonS3 전체(수십 개
/// 멤버)가 아니라 이 인터페이스에만 의존하게 해서, 목 라이브러리 없이도 테스트에서
/// 인메모리 가짜 구현으로 병합 로직/종료 코드 매핑을 검증할 수 있게 한다. 실제 구현
/// (<see cref="AwsBundleObjectStore"/>)만 IAmazonS3를 알고, 그 인스턴스는 Program.Main만
/// 생성한다.</summary>
public interface IBundleObjectStore
{
	/// <summary>오브젝트가 없으면 <see cref="Amazon.S3.AmazonS3Exception"/>(404)를,
	/// 권한이 없으면 같은 타입(403)을 던진다 - 실제 S3 동작을 그대로 재현한다.</summary>
	Task<string> GetObjectContentAsync(string bucket, string key, CancellationToken cancellationToken);
}

namespace S3EnvManager.Cli;

/// <summary>PLAN-cli.md "종료 코드" 절에 정의된 종료 코드. 값 자체가 계약이므로 숫자를
/// 바꾸지 않는다 - 배포 스크립트가 $LASTEXITCODE로 이 값을 직접 분기한다.</summary>
public enum ExitCode
{
	Success = 0,
	ArgumentError = 1,
	AwsPermissionDenied = 2,
	BundleMissing = 3,
	IntegrityFailure = 4,
	KeyMissing = 5,

	/// <summary>403/404 외의 S3/KMS 호출 실패(5xx, 스로틀링, 네트워크, 만료된 토큰 등) -
	/// 일시적일 수 있으므로 재시도 여지가 있음을 뜻한다. AwsPermissionDenied/BundleMissing과
	/// 달리 "무엇을 해야 할지"가 명확하지 않은 나머지 AWS 실패를 여기로 몰아, 처리되지 않은
	/// 예외로 스택트레이스가 새는 대신 최소한 분류된 종료 코드로 실패하게 한다.</summary>
	AwsCallFailed = 6,

	/// <summary>get-all --format dotenv인데 값에 개행이 포함돼 dotenv로 표현할 수 없는
	/// 경우 - 인자/설정 오류(1)가 아니라 조회된 데이터의 문제이므로 별도 코드로 구분한다.</summary>
	OutputFormatError = 7,

	/// <summary>위 어느 코드로도 분류되지 않은 처리되지 않은 예외(예상 못한 버그) -
	/// ArgumentError(1)와 의미가 다르므로 재사용하지 않는다: 1은 "호출자가 인자/환경변수를
	/// 다시 확인하면 되는 문제"인데, 이 코드는 "그걸로는 설명이 안 되는 내부 오류"다.</summary>
	UnexpectedError = 8,
}

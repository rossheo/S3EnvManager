namespace S3EnvManager.Sops;

/// <summary>SopsDotEnvDocument는 `KEY=값` 줄 기반 포맷이라, 키 이름 자체에 `=`나 줄바꿈이
/// 섞이면 직렬화/파싱 왕복이 깨진다(값 부분이 암호화되지 않은 채로 끼어들어 다음 로드 때
/// FormatException을 던지거나, 최악의 경우 다른 키의 값을 가려버릴 수 있다). `sops_` 접두사는
/// 파서가 메타데이터 줄로 해석하는 예약어라 일반 키로 쓸 수 없다. 저장 파이프라인(서버 사이드)
/// 진입점에서 검증해야 한다 - Blazor Server는 클라이언트가 보낸 문자열을 그대로 바인딩하므로,
/// 화면의 입력 제약만으로는 조작된 클라이언트를 막을 수 없다.</summary>
public static class SecretKeyNameValidator
{
	private const string ReservedMetadataPrefix = "sops_";

	/// <summary>유효하면 null, 아니면 사용자에게 보여줄 오류 메시지.</summary>
	public static string? Validate(string? key)
	{
		if (string.IsNullOrEmpty(key))
		{
			return "키 이름을 입력하세요.";
		}
		if (key.Contains('=', StringComparison.Ordinal))
		{
			return "키 이름에 '='를 포함할 수 없습니다(sops dotenv 포맷의 키/값 구분자와 충돌합니다).";
		}
		if (key.Contains('\n') || key.Contains('\r'))
		{
			return "키 이름에 줄바꿈 문자를 포함할 수 없습니다.";
		}
		if (key.StartsWith(ReservedMetadataPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return $"키 이름은 '{ReservedMetadataPrefix}'로 시작할 수 없습니다(sops 메타데이터 접두사로 예약되어 있습니다).";
		}
		return null;
	}
}
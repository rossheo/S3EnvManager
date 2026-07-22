using System.Text.Json;
using S3EnvManager.Web.Services;
using Xunit;

namespace S3EnvManager.Web.Tests;

public class AuditJsonOptionsTests
{
	[Fact]
	public void Default_DoesNotEscapeKorean()
	{
		var json = JsonSerializer.Serialize(new { detail = "자격증명 확인" }, AuditJsonOptions.Default);

		Assert.Contains("자격증명 확인", json);
		Assert.DoesNotContain("\\u", json);
	}

	// 이스케이프 완화 대상은 non-ASCII 문자 범위이지 HTML 특수문자가 아님을 확인한다.
	[Fact]
	public void Default_StillEscapesHtmlSensitiveCharacters()
	{
		var json = JsonSerializer.Serialize(new { detail = "<script>" }, AuditJsonOptions.Default);

		Assert.DoesNotContain("<script>", json);
	}
}
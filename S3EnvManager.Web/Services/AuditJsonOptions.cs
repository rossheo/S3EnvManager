using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace S3EnvManager.Web.Services;

/// <summary>Blazor가 렌더링 시 HTML 이스케이프를 별도로 하므로 non-ASCII 이스케이프를 완화해도 안전하다.</summary>
public static class AuditJsonOptions
{
	public static readonly JsonSerializerOptions Default =
		new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };
}
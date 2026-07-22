using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace S3EnvManager.Sops;

/// <summary>sops dotenv 포맷의 원시 표현(getsops/sops v3.13.2 stores/dotenv, stores/metadata 재현).</summary>
public sealed partial class SopsDotEnvDocument
{
	private const string SopsPrefix = "sops_";
	private const string FormatVersion = "3.13.2";
	private const string UnencryptedSuffixDefault = "_unencrypted";

	public List<KeyValuePair<string, string>> Entries { get; } = [];

	public List<SopsKmsEntry> KmsEntries { get; } = [];

	public DateTimeOffset LastModified { get; set; }

	/// <summary>sops_mac 트레일러의 원시 값(암호화된 "ENC[...]" 문자열).</summary>
	public string EncryptedMac { get; set; } = string.Empty;

	[GeneratedRegex(@"^kms__list_(?<index>\d+)__map_(?<field>.+)$")]
	private static partial Regex KmsListFieldPattern();

	public static SopsDotEnvDocument Parse(string fileContent)
	{
		var document = new SopsDotEnvDocument();
		var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);

		foreach (var rawLine in fileContent.Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			if (line.Length == 0)
			{
				continue;
			}
			if (line[0] == '#')
			{
				continue;
			}

			var separatorIndex = line.IndexOf('=');
			if (separatorIndex < 0)
			{
				throw new FormatException($"잘못된 dotenv 줄입니다(= 없음): {line}");
			}

			var key = line[..separatorIndex];
			var value = line[(separatorIndex + 1)..];

			if (key.StartsWith(SopsPrefix, StringComparison.Ordinal))
			{
				metadata[key[SopsPrefix.Length..]] = value;
			}
			else
			{
				document.Entries.Add(new KeyValuePair<string, string>(key, value));
			}
		}

		if (metadata.Count == 0)
		{
			throw new FormatException("sops 메타데이터(sops_* 줄)를 찾을 수 없습니다 - 암호화된 파일이 아닙니다.");
		}

		var kmsByIndex = new SortedDictionary<Int32, Dictionary<string, string>>();
		var contextByIndex = new Dictionary<Int32, Dictionary<string, string>>();

		foreach (var (key, value) in metadata)
		{
			var match = KmsListFieldPattern().Match(key);
			if (!match.Success)
			{
				continue;
			}

			var index = Int32.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
			var field = match.Groups["field"].Value;

			const string ContextPrefix = "context__map_";
			if (field.StartsWith(ContextPrefix, StringComparison.Ordinal))
			{
				var contextKey = field[ContextPrefix.Length..];
				if (!contextByIndex.TryGetValue(index, out var contextMap))
				{
					contextMap = [];
					contextByIndex[index] = contextMap;
				}
				contextMap[contextKey] = value;
				continue;
			}

			if (!kmsByIndex.TryGetValue(index, out var fields))
			{
				fields = [];
				kmsByIndex[index] = fields;
			}
			fields[field] = value;
		}

		foreach (var (index, fields) in kmsByIndex)
		{
			contextByIndex.TryGetValue(index, out var context);
			document.KmsEntries.Add(new SopsKmsEntry(
				Arn: fields.GetValueOrDefault("arn", string.Empty),
				CiphertextBlob: Convert.FromBase64String(fields.GetValueOrDefault("enc", string.Empty)),
				CreatedAt: DateTimeOffset.Parse(
					fields["created_at"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
				EncryptionContext: (IReadOnlyDictionary<string, string>?)context ?? new Dictionary<string, string>()));
		}

		document.LastModified = DateTimeOffset.Parse(
			metadata["lastmodified"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
		document.EncryptedMac = metadata["mac"];

		return document;
	}

	public string Serialize()
	{
		var builder = new StringBuilder();
		foreach (var (key, value) in Entries)
		{
			builder.Append(key).Append('=').Append(value).Append('\n');
		}

		var metadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
		for (var index = 0; index < KmsEntries.Count; index++)
		{
			var entry = KmsEntries[index];
			var prefix = $"kms__list_{index}__map_";
			metadata[$"{prefix}arn"] = entry.Arn;
			metadata[$"{prefix}aws_profile"] = string.Empty;
			metadata[$"{prefix}created_at"] = FormatRfc3339(entry.CreatedAt);
			metadata[$"{prefix}enc"] = Convert.ToBase64String(entry.CiphertextBlob);
			foreach (var (contextKey, contextValue) in entry.EncryptionContext)
			{
				metadata[$"{prefix}context__map_{contextKey}"] = contextValue;
			}
		}
		metadata["lastmodified"] = FormatRfc3339(LastModified);
		metadata["mac"] = EncryptedMac;
		metadata["unencrypted_suffix"] = UnencryptedSuffixDefault;
		metadata["version"] = FormatVersion;

		foreach (var (key, value) in metadata)
		{
			builder.Append(SopsPrefix).Append(key).Append('=').Append(value).Append('\n');
		}

		return builder.ToString();
	}

	/// <summary>Go의 time.RFC3339(초 단위, UTC "Z" 표기)와 동일한 포맷으로 맞춘다.</summary>
	internal static string FormatRfc3339(DateTimeOffset value) =>
		value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
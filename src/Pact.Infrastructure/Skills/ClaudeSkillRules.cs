using System.Text;
using System.Text.Json;

namespace Pact.Infrastructure.Skills;

/// <summary>Changes only the JSON tokens needed to update a Claude skill override.</summary>
internal static class ClaudeSkillRules
{
	internal static string? Edit(string? original, string command, string? value)
	{
		if (original is null && value is null)
		{
			return null;
		}
		var utf8 = Encoding.UTF8.GetBytes(original ?? "{}");
		var root = ReadObject(utf8, 0);
		var overrides = root.FindIndex(property => property.Name == "skillOverrides");
		var property = overrides >= 0 ? root[overrides] : null;
		var members = property is null || utf8[property.ValueStart] == 'n' ? null : ReadObject(utf8, property.ValueStart);
		var member = members?.FindIndex(item => item.Name == command) ?? -1;
		if (value is null)
		{
			if (member < 0)
			{
				return original;
			}
			return members!.Count == 1 ? Remove(utf8, root, overrides) : Remove(utf8, members, member);
		}
		var serialized = JsonSerializer.Serialize(value);
		if (member >= 0)
		{
			var matched = members![member];
			if (utf8[matched.ValueStart] == '"' &&
				JsonSerializer.Deserialize<string>(utf8.AsSpan(matched.ValueStart, matched.End - matched.ValueStart)) == value)
			{
				return original;
			}
			return Replace(utf8, matched.ValueStart, matched.End, serialized);
		}
		var entry = JsonSerializer.Serialize(command) + ": " + serialized;
		if (members is not null)
		{
			return Add(utf8, members, property!.ValueStart, entry);
		}
		if (property is not null)
		{
			return Replace(utf8, property.ValueStart, property.End, "{" + entry + "}");
		}
		return Add(utf8, root, Array.IndexOf(utf8, (byte)'{'), "\"skillOverrides\": {" + entry + "}");
	}

	private static List<Property> ReadObject(byte[] utf8, int offset)
	{
		var reader = new Utf8JsonReader(utf8.AsSpan(offset));
		if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
		{
			throw new JsonException("Expected a settings object.");
		}
		var properties = new List<Property>();
		var names = new HashSet<string>(StringComparer.Ordinal);
		while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
		{
			var name = reader.GetString()!;
			if (!names.Add(name))
			{
				throw new JsonException($"Duplicate settings property: {name}.");
			}
			var start = offset + (int)reader.TokenStartIndex;
			reader.Read();
			var value = offset + (int)reader.TokenStartIndex;
			reader.Skip();
			properties.Add(new(name, start, value, offset + (int)reader.BytesConsumed));
		}
		if (reader.TokenType != JsonTokenType.EndObject || offset == 0 && reader.Read())
		{
			throw new JsonException("Expected a settings object.");
		}
		return properties;
	}

	private static string Add(byte[] utf8, List<Property> properties, int objectStart, string entry)
	{
		var insertion = properties.Count == 0 ? objectStart + 1 : properties[^1].End;
		return Replace(utf8, insertion, insertion, properties.Count == 0 ? entry : ", " + entry);
	}

	private static string Remove(byte[] utf8, List<Property> properties, int index)
	{
		var current = properties[index];
		return index + 1 < properties.Count
			? Replace(utf8, current.Start, properties[index + 1].Start, "")
			: Replace(utf8, index == 0 ? current.Start : properties[index - 1].End, current.End, "");
	}

	private static string Replace(byte[] utf8, int start, int end, string text) =>
		Encoding.UTF8.GetString(utf8.AsSpan(0, start)) + text + Encoding.UTF8.GetString(utf8.AsSpan(end));

	private sealed record Property(string Name, int Start, int ValueStart, int End);
}

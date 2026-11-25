using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Tomlyn;
using Tomlyn.Model;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	/// <summary>Previews installing an existing definition directly into the selected agents' user configuration files.</summary>
	public static McpNativeEditPreview PreviewNativeShare(AgentProfileRecord owner, NativeMcpServer source, IReadOnlyList<AgentProfileRecord> targets)
	{
		ArgumentNullException.ThrowIfNull(owner);
		ArgumentNullException.ThrowIfNull(targets);
		var sourceContent = ReadNativeSource(owner, source);
		var original = ReadNativeDefinition(owner.Kind, source, sourceContent);
		var sourcePath = SkillCatalogService.ResolveManifest(source.SourcePath);
		List<McpNativeFileEdit> edits = [];
		foreach (var group in targets.GroupBy(profile => SkillCatalogService.ResolveManifest(NativeUserConfigurationPath(profile)), StringComparer.OrdinalIgnoreCase))
		{
			var path = group.Key;
			if (StringComparer.OrdinalIgnoreCase.Equals(path, sourcePath))
			{
				continue;
			}
			var kind = group.First().Kind;
			var definition = owner.Kind == kind ? original : ConvertNativeDeclaration(original, source.Transport, kind);
			var expected = ReadFile(path);
			var updated = kind == AgentKind.Codex ? InstallCodexDefinition(expected, source.Name, definition) :
				InstallClaudeDefinition(expected, source.Name, definition);
			if (updated is not null && updated != expected)
			{
				edits.Add(new(path, expected, updated));
			}
		}
		return new(source.SourcePath, sourceContent, edits.ToArray());
	}

	private static JsonObject ReadNativeDefinition(AgentKind kind, NativeMcpServer source, string content)
	{
		try
		{
			if (kind == AgentKind.Codex)
			{
				var model = Toml.ToModel(content);
				if (model.TryGetValue("mcp_servers", out var value) && value is TomlTable servers &&
					servers.TryGetValue(source.Name, out var definition) && JsonSerializer.SerializeToNode(definition) is JsonObject item)
				{
					return item;
				}
			}
			else if (ClaudeDeclarations(ParseNativeJson(content), source)?[source.Name] is JsonObject item)
			{
				return item;
			}
		}
		catch (TomlException exception)
		{
			throw new InvalidOperationException("The native MCP configuration is invalid TOML.", exception);
		}
		throw MissingNativeDeclaration();
	}

	private static string? InstallCodexDefinition(string? content, string name, JsonObject definition)
	{
		var updated = EditCodexDeclaration(content, name, (servers, key) =>
		{
			if (servers.TryGetValue(key, out var current))
			{
				if (JsonNode.DeepEquals(JsonSerializer.SerializeToNode(current), definition))
				{
					return false;
				}
				throw ConflictingNativeName(name);
			}
			servers.Add(key, ToTomlTable(definition));
			return true;
		});
		return content is null && updated.Length == 0 ? null : updated;
	}

	private static string? InstallClaudeDefinition(string? content, string name, JsonObject definition)
	{
		var root = ParseNativeJson(content);
		if (root["mcpServers"] is null)
		{
			root["mcpServers"] = new JsonObject();
		}
		if (root["mcpServers"] is not JsonObject servers)
		{
			throw new InvalidOperationException("The Claude mcpServers setting is not an object.");
		}
		if (servers.TryGetPropertyValue(name, out var current))
		{
			if (JsonNode.DeepEquals(current, definition))
			{
				return content;
			}
			throw ConflictingNativeName(name);
		}
		servers.Add(name, definition.DeepClone());
		return FormatNativeJson(root, content);
	}

	private static InvalidOperationException ConflictingNativeName(string name) =>
		new($"A destination profile already defines {name} differently. Choose another profile or review the existing declaration first.");

	private static void RequirePortableFields(JsonObject definition, string[] fields)
	{
		if (definition.Any(field => !fields.Contains(field.Key, StringComparer.Ordinal)))
		{
			throw new InvalidOperationException("The declaration uses agent-specific settings. Choose a profile of the same agent or configure those settings in the destination agent.");
		}
	}

	private static JsonObject ConvertNativeDeclaration(JsonObject source, string transport, AgentKind target)
	{
		if (target == AgentKind.Claude && source["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var active) && !active)
		{
			throw new InvalidOperationException("The source server is disabled in Codex. Enable it before sharing it with Claude.");
		}
		if (transport == "HTTP")
		{
			var result = new JsonObject { ["url"] = source["url"]?.DeepClone() ?? throw MissingNativeDeclaration() };
			if (target == AgentKind.Claude)
			{
				RequirePortableFields(source, ["url", "bearer_token_env_var", "http_headers", "env_http_headers", "enabled"]);
				result["type"] = "http";
				CopyCodexHeadersToClaude(source, result);
			}
			else
			{
				RequirePortableFields(source, ["type", "url", "headers"]);
				CopyClaudeHeadersToCodex(source, result);
			}
			return result;
		}
		if (transport != "stdio")
		{
			throw new InvalidOperationException("The destination agent supports shared streamable HTTP and stdio declarations.");
		}
		var stdio = new JsonObject { ["command"] = source["command"]?.DeepClone() ?? throw MissingNativeDeclaration() };
		if (source["args"] is { } arguments)
		{
			stdio["args"] = arguments.DeepClone();
		}
		var environment = ReadStringMap(source["env"]);
		if (target == AgentKind.Claude)
		{
			RequirePortableFields(source, ["command", "args", "env", "env_vars", "enabled"]);
			stdio["type"] = "stdio";
			foreach (var name in source["env_vars"]?.Deserialize<string[]>() ?? [])
			{
				environment.TryAdd(name, EnvironmentReference(name));
			}
		}
		else
		{
			RequirePortableFields(source, ["type", "command", "args", "env"]);
			JsonArray inherited = [];
			foreach (var (name, value) in environment.ToArray())
			{
				if (!TryReadEnvReference(value, out var variable))
				{
					RequireLiteral(value);
					continue;
				}
				if (variable != name)
				{
					throw new InvalidOperationException("Codex requires inherited environment variables to use their original names.");
				}
				inherited.Add(name);
				environment.Remove(name);
			}
			if (inherited.Count > 0)
			{
				stdio["env_vars"] = inherited;
			}
		}
		WriteStringMap(stdio, "env", environment);
		return stdio;
	}

	private static void CopyCodexHeadersToClaude(JsonObject source, JsonObject result)
	{
		var headers = ReadStringMap(source["http_headers"], StringComparer.OrdinalIgnoreCase);
		foreach (var (name, variable) in ReadStringMap(source["env_http_headers"]))
		{
			headers[name] = EnvironmentReference(variable);
		}
		if (source["bearer_token_env_var"] is { } token)
		{
			headers["Authorization"] = "Bearer " + EnvironmentReference(token.GetValue<string>());
		}
		WriteStringMap(result, "headers", headers);
	}

	private static void CopyClaudeHeadersToCodex(JsonObject source, JsonObject result)
	{
		var literal = ReadStringMap(source["headers"]);
		Dictionary<string, string> inherited = new(StringComparer.OrdinalIgnoreCase);
		foreach (var (name, value) in literal.ToArray())
		{
			if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && value.StartsWith("Bearer ", StringComparison.Ordinal) &&
				TryReadEnvReference(value[7..], out var bearer))
			{
				result["bearer_token_env_var"] = bearer;
				literal.Remove(name);
			}
			else if (TryReadEnvReference(value, out var variable))
			{
				inherited.Add(name, variable);
				literal.Remove(name);
			}
			else
			{
				RequireLiteral(value);
			}
		}
		WriteStringMap(result, "http_headers", literal);
		WriteStringMap(result, "env_http_headers", inherited);
	}

	private static bool TryReadEnvReference(string value, out string name)
	{
		name = value.StartsWith("${", StringComparison.Ordinal) && value.EndsWith('}') ? value[2..^1] : "";
		return VariablePattern().IsMatch(name);
	}

	private static void RequireLiteral(string value)
	{
		if (value.Contains("${", StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The destination agent requires a complete environment variable reference for this value.");
		}
	}

	private static Dictionary<string, string> ReadStringMap(JsonNode? node, StringComparer? comparer = null) => node switch
	{
		null => new(comparer ?? StringComparer.Ordinal),
		JsonObject map => map.ToDictionary(pair => pair.Key, pair => pair.Value?.GetValue<string>() ??
			throw new InvalidOperationException("An MCP header or environment variable is missing its value."), comparer ?? StringComparer.Ordinal),
		_ => throw new InvalidOperationException("The MCP headers or environment variables must be an object.")
	};

	private static void WriteStringMap(JsonObject result, string key, Dictionary<string, string> values)
	{
		if (values.Count > 0)
		{
			result[key] = new JsonObject(values.Select(pair => KeyValuePair.Create<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value))));
		}
	}

	private static TomlTable ToTomlTable(JsonObject node)
	{
		var table = new TomlTable();
		foreach (var (key, value) in node)
		{
			table.Add(key, ToTomlValue(value));
		}
		return table;
	}

	private static object ToTomlValue(JsonNode? node)
	{
		if (node is JsonArray items)
		{
			TomlArray array = [];
			foreach (var item in items)
			{
				array.Add(ToTomlValue(item));
			}
			return array;
		}
		return node switch
		{
			JsonObject obj => ToTomlTable(obj),
			JsonValue value when value.TryGetValue<string>(out var text) => text,
			JsonValue value when value.TryGetValue<bool>(out var boolean) => boolean,
			JsonValue value when value.TryGetValue<long>(out var integer) => integer,
			JsonValue value when value.TryGetValue<double>(out var number) => number,
			_ => throw new InvalidOperationException("The native MCP declaration contains a value that Codex cannot represent.")
		};
	}
}

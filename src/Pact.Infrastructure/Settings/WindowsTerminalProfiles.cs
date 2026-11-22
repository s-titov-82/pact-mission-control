using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Pact.Infrastructure.Settings;

/// <summary>Reads Windows Terminal profile appearance from JSONC without modifying the source.</summary>
public sealed class WindowsTerminalProfiles
{
	private readonly JsonObject _defaults;
	private readonly JsonObject[] _schemes;
	private readonly JsonObject[] _profiles;

	/// <summary>Lists profiles in file order, keeping identically named entries distinct by index.</summary>
	public IReadOnlyList<string> Names { get; }

	/// <summary>Loads user schemes, profile defaults and per-profile overrides.</summary>
	public WindowsTerminalProfiles(string json)
	{
		var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
		{ AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
			?? throw new FormatException("Expected Windows Terminal settings.json.");
		var profiles = root["profiles"];
		_defaults = (profiles as JsonObject)?["defaults"] as JsonObject ?? [];
		var list = profiles as JsonArray ?? (profiles as JsonObject)?["list"] as JsonArray
			?? throw new FormatException("The settings file has no profile list.");
		_profiles = list.OfType<JsonObject>().ToArray();
		Names = _profiles.Select(p => (string?)p["name"] ?? (string?)p["guid"] ?? "Unnamed profile").ToArray();
		using var stream = typeof(WindowsTerminalProfiles).Assembly.GetManifestResourceStream(
			"Pact.Infrastructure.Settings.WindowsTerminalSchemes.json")!;
		var builtIns = JsonNode.Parse(stream)!.AsArray();
		_schemes = [.. (root["schemes"] as JsonArray ?? []).OfType<JsonObject>(), .. builtIns.OfType<JsonObject>()];
	}

	/// <summary>Resolves the selected profile and converts Windows Terminal points to CSS pixels.</summary>
	public TerminalAppearance Import(int index, bool dark)
	{
		var profile = _profiles[index];
		JsonNode? Value(string name)
		{
			return profile[name] ?? _defaults[name];
		}
		JsonNode? Font(string name)
		{
			return (profile["font"] as JsonObject)?[name] ?? (_defaults["font"] as JsonObject)?[name];
		}
		var selection = Value("colorScheme");
		var schemeName = (string?)(selection is JsonObject pair ? pair[dark ? "dark" : "light"] : selection) ?? "Campbell";
		var scheme = _schemes.FirstOrDefault(s => string.Equals((string?)s["name"], schemeName, StringComparison.OrdinalIgnoreCase))
			?? throw new FormatException($"Color scheme '{schemeName}' was not found.");
		var colors = new Dictionary<string, string>();
		foreach (var key in TerminalAppearance.ColorNames)
		{
			var source = key switch { "magenta" => "purple", "brightMagenta" => "brightPurple", "cursor" => "cursorColor", _ => key };
			if ((string?)(Value(source) ?? scheme[source]) is { } color)
			{
				colors[key] = color.Length == 4 && color[0] == '#'
					? $"#{color[1]}{color[1]}{color[2]}{color[2]}{color[3]}{color[3]}" : color;
			}
		}
		var height = Font("cellHeight")?.ToString();
		var appearance = new TerminalAppearance
		{
			FontFamily = (string?)Font("face") ?? (string?)Value("fontFace") ?? "Cascadia Mono",
			FontSize = ((double?)(Font("size") ?? Value("fontSize")) ?? 12) * 96 / 72,
			LineHeight = double.TryParse(height, CultureInfo.InvariantCulture, out var scale) ? Math.Max(1, scale) : 1,
			Colors = colors
		};
		return appearance.IsValid ? appearance : throw new FormatException("The profile contains unsupported font dimensions or colors.");
	}
}

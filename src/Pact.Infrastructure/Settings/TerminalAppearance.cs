using System.Text.Json.Serialization;

namespace Pact.Infrastructure.Settings;

/// <summary>Rendering preferences shared by the settings editor and terminal host.</summary>
public sealed record TerminalAppearance
{
	/// <summary>Font family or CSS fallback list.</summary>
	public string FontFamily { get; init; } = "Cascadia Mono, Consolas, monospace";
	/// <summary>Font size in device-independent pixels.</summary>
	public double FontSize { get; init; } = 15;
	/// <summary>Line height relative to the font's normal cell height.</summary>
	public double LineHeight { get; init; } = 1.1;
	/// <summary>Optional xterm palette overrides in hexadecimal RGB notation.</summary>
	public Dictionary<string, string> Colors { get; init; } = [];
	/// <summary>Whether this configuration follows all Pact terminal defaults.</summary>
	[JsonIgnore]
	public bool UsesDefaults => FontFamily == "Cascadia Mono, Consolas, monospace" && FontSize == 15 && LineHeight == 1.1 && Colors.Count == 0;
	/// <summary>Palette fields supported by both the editor and the renderer.</summary>
	public static IReadOnlyList<string> ColorNames { get; } = ["foreground", "background", "cursor", "selectionBackground",
		"black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
		"brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite"];
	/// <summary>Whether all supplied values can be applied by the terminal renderer.</summary>
	[JsonIgnore]
	public bool IsValid => !string.IsNullOrWhiteSpace(FontFamily) && FontSize is >= 4 and <= 128
		&& LineHeight is >= 1 and <= 4 && Colors is not null && Colors.All(entry =>
			ColorNames.Contains(entry.Key) && entry.Value is { Length: 7 } color && color[0] == '#' && color[1..].All(Uri.IsHexDigit));
}

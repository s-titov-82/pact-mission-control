using Avalonia.Styling;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;
using TextMateSharp.Grammars;

namespace Pact.App.Avalonia.Views.Diff;

/// <summary>Syntax colouring of an editor by file name, in the app's light or dark theme.</summary>
internal static class DiffSyntax
{
	private static readonly RegistryOptions Registry = new(ThemeName.DarkPlus);

	/// <summary>The grammar scope for a file's extension; <see langword="null"/> for unknown files.</summary>
	public static string? ScopeFor(string path)
	{
		var extension = Path.GetExtension(path);
		return string.IsNullOrEmpty(extension)
			? null
			: Registry.GetScopeByExtension(extension.ToLowerInvariant());
	}

	/// <summary>Installs colouring on an editor, following the app theme.</summary>
	public static TextMate.Installation Install(TextEditor editor)
	{
		ArgumentNullException.ThrowIfNull(editor);
		var installation = editor.InstallTextMate(Registry);
		ApplyTheme(editor, installation);
		editor.ActualThemeVariantChanged += (_, _) => ApplyTheme(editor, installation);
		return installation;
	}

	/// <summary>Colours the editor's text as the file's language, or not at all.</summary>
	public static void SetFile(TextMate.Installation installation, string? path)
	{
		ArgumentNullException.ThrowIfNull(installation);
		installation.SetGrammar(path is null ? null : ScopeFor(path));
	}

	private static void ApplyTheme(TextEditor editor, TextMate.Installation installation) =>
		installation.SetTheme(Registry.LoadTheme(
			editor.ActualThemeVariant == ThemeVariant.Light ? ThemeName.LightPlus : ThemeName.DarkPlus));
}

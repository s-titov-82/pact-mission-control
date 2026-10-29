using System.Collections.Concurrent;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Pact.App.Avalonia.Views.Editing;

/// <summary>
/// Grammars and themes read once per process and handed to every editor. Each editor's registry
/// copies a grammar before it compiles it, so one read serves them all; read anew, Markdown alone
/// with its embedded languages costs tens of megabytes per editor.
/// </summary>
internal sealed class SharedRegistryOptions(RegistryOptions source) : IRegistryOptions
{
	private readonly ConcurrentDictionary<string, IRawGrammar> _grammars = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<string, IRawTheme> _themes = new(StringComparer.Ordinal);
	private readonly ConcurrentDictionary<ThemeName, IRawTheme> _namedThemes = new();
	private readonly Lazy<IRawTheme> _defaultTheme = new(source.GetDefaultTheme);

	/// <summary>The grammar scope for a file extension such as ".md"; <see langword="null"/> when unknown.</summary>
	public string? GetScopeByExtension(string extension) => source.GetScopeByExtension(extension);

	/// <summary>One of the bundled themes.</summary>
	public IRawTheme LoadTheme(ThemeName name) => _namedThemes.GetOrAdd(name, source.LoadTheme);

	public IRawGrammar GetGrammar(string scopeName) => _grammars.GetOrAdd(scopeName, source.GetGrammar);

	public IRawTheme GetTheme(string scopeName) => _themes.GetOrAdd(scopeName, source.GetTheme);

	public ICollection<string> GetInjections(string scopeName) => source.GetInjections(scopeName);

	public IRawTheme GetDefaultTheme() => _defaultTheme.Value;
}

using System.Text;
using System.Text.RegularExpressions;

namespace Pact.Presentation.Services;

/// <summary>
/// Recognizes, in a client's output, that a paste it made from the clipboard has landed in its
/// composer: either a collapsed-paste placeholder (<c>[Pasted …</c>) or the end of the pasted
/// text itself. A client pastes from the clipboard asynchronously, and a key press that
/// arrives first can cancel the paste, so a submit must wait for this evidence. Escape
/// sequences and whitespace are ignored, so line wrapping and cursor moves between rows
/// cannot hide the match. Feed it text that has already passed
/// <see cref="TerminalDisplayOutputFilter"/>, which keeps escape sequences whole per chunk.
/// </summary>
public sealed partial class ClientPasteConfirmation
{
	private const string PlaceholderMarker = "[Pasted";
	private const int TailLength = 12;
	private const int MaximumRetainedLength = 64 * 1024;

	private readonly string _tail;
	private readonly StringBuilder _seen = new();

	/// <summary>Starts watching for a paste of <paramref name="text"/>.</summary>
	public ClientPasteConfirmation(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		var normalized = Normalize(text);
		_tail = normalized.Length <= TailLength ? normalized : normalized[^TailLength..];
	}

	/// <summary>
	/// Whether the paste needs no evidence because its text has no visible characters.
	/// </summary>
	public bool IsTriviallyConfirmed => _tail.Length == 0;

	/// <summary>
	/// Adds a chunk of client output and reports whether the paste is now visible.
	/// </summary>
	public bool Observe(string output)
	{
		if (IsTriviallyConfirmed)
		{
			return true;
		}

		if (string.IsNullOrEmpty(output))
		{
			return false;
		}

		_seen.Append(Normalize(output));
		var seen = _seen.ToString();
		if (seen.Contains(PlaceholderMarker, StringComparison.Ordinal)
			|| seen.Contains(_tail, StringComparison.Ordinal))
		{
			return true;
		}

		var keep = Math.Max(_tail.Length, PlaceholderMarker.Length);
		if (_seen.Length > MaximumRetainedLength)
		{
			_seen.Remove(0, _seen.Length - keep);
		}

		return false;
	}

	private static string Normalize(string text)
	{
		var withoutSequences = EscapeSequence().Replace(text, string.Empty);
		var builder = new StringBuilder(withoutSequences.Length);
		foreach (var character in withoutSequences)
		{
			if (!char.IsWhiteSpace(character) && !char.IsControl(character))
			{
				builder.Append(character);
			}
		}

		return builder.ToString();
	}

	// CSI sequences, OSC strings ended by BEL or ST, and two-character escapes.
	[GeneratedRegex(
		@"\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b[@-_]",
		RegexOptions.CultureInvariant)]
	private static partial Regex EscapeSequence();
}

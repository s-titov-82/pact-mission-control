using System.Diagnostics.CodeAnalysis;

namespace Pact.Presentation.Services;

/// <summary>
/// Tracks whether a session's client has ConPTY win32-input-mode enabled by
/// scanning its output stream for ESC[?9001h / ESC[?9001l. Feed it text that
/// has already passed TerminalDisplayOutputFilter: the filter's carry logic
/// guarantees escape sequences are never split across the scanned chunks.
/// </summary>
public sealed class Win32InputModeTracker
{
	private const char Escape = (char)0x1b;

	private static readonly string EnableSequence = $"{Escape}[?9001h";
	private static readonly string DisableSequence = $"{Escape}[?9001l";
	private readonly Lock _gate = new();
	[SuppressMessage(
		"Style",
		"IDE0032:Use auto property",
		Justification = "The backing field is protected by the tracker lock for cross-thread output and input access.")]
	private bool _isActive;

	/// <summary>
	/// Whether the client currently has win32-input-mode enabled. When set, a bare newline
	/// loses its modifier, so Shift+Enter must be sent as an encoded key event instead.
	/// </summary>
	public bool IsActive
	{
		get
		{
			lock (_gate)
			{
				return _isActive;
			}
		}
	}

	/// <summary>
	/// Scans a chunk of already-filtered output for the mode toggles, keeping only the last
	/// toggle in the chunk so a sequence that enables then disables within one chunk resolves
	/// correctly.
	/// </summary>
	public void Scan(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return;
		}

		var lastEnable = text.LastIndexOf(EnableSequence, StringComparison.Ordinal);
		var lastDisable = text.LastIndexOf(DisableSequence, StringComparison.Ordinal);
		if (lastEnable < 0 && lastDisable < 0)
		{
			return;
		}

		lock (_gate)
		{
			_isActive = lastEnable > lastDisable;
		}
	}
}
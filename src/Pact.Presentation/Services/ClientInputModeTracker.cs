using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Pact.Presentation.Services;

/// <summary>
/// Tracks the input modes a session's client has requested by scanning its output for DEC
/// private mode sets and resets: win32-input-mode (9001), mouse tracking (1000, 1002, 1003)
/// and SGR mouse encoding (1006). ConPTY may combine modes in one sequence, for example
/// <c>ESC[?1003;1006h</c>. Feed it text that has already passed
/// <see cref="TerminalDisplayOutputFilter"/>, whose carry logic keeps escape sequences whole
/// across the scanned chunks.
/// </summary>
[SuppressMessage(
	"Style",
	"IDE0032:Use auto property",
	Justification = "The mode fields are protected by the tracker lock for cross-thread output and input access.")]
public sealed partial class ClientInputModeTracker
{
	private readonly Lock _gate = new();
	private bool _win32Input;
	// The three tracking modes are set and reset independently; any one of them reports buttons.
	private readonly HashSet<string> _mouseTrackingModes = [];
	private bool _sgrMouseEncoding;

	/// <summary>
	/// Whether the client has win32-input-mode enabled. When set, a bare newline loses its
	/// modifier, so Enter, Shift+Enter and Esc must be sent as encoded key events.
	/// </summary>
	public bool IsWin32InputActive
	{
		get
		{
			lock (_gate)
			{
				return _win32Input;
			}
		}
	}

	/// <summary>
	/// Whether the client tracks mouse buttons with SGR encoding, so an encoded mouse event
	/// written to its input reaches it as a real mouse event.
	/// </summary>
	public bool IsSgrMouseActive
	{
		get
		{
			lock (_gate)
			{
				return _mouseTrackingModes.Count > 0 && _sgrMouseEncoding;
			}
		}
	}

	/// <summary>
	/// Applies every mode set or reset in a chunk of already-filtered output, in order, so a
	/// chunk that enables then disables a mode resolves to disabled.
	/// </summary>
	public void Scan(string text)
	{
		if (string.IsNullOrEmpty(text) || !text.Contains("\u001b[?", StringComparison.Ordinal))
		{
			return;
		}

		lock (_gate)
		{
			foreach (Match match in PrivateModeSequence().Matches(text))
			{
				var enabled = match.Groups["action"].Value == "h";
				foreach (var mode in match.Groups["modes"].Value.Split(';'))
				{
					switch (mode)
					{
						case "9001":
							_win32Input = enabled;
							break;
						case "1000" or "1002" or "1003":
							_ = enabled ? _mouseTrackingModes.Add(mode) : _mouseTrackingModes.Remove(mode);
							break;
						case "1006":
							_sgrMouseEncoding = enabled;
							break;
					}
				}
			}
		}
	}

	[GeneratedRegex(@"\u001b\[\?(?<modes>[0-9;]+)(?<action>[hl])", RegexOptions.CultureInvariant)]
	private static partial Regex PrivateModeSequence();
}

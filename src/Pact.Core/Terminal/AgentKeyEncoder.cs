using Pact.Core.Agents;

namespace Pact.Core.Terminal;

/// <summary>
/// Encodes the keys Pact sends to an agent's terminal and recognizes a submit among written input.
/// </summary>
/// <remarks>
/// Codex reads console key records once it enables win32-input-mode: a bare carriage return then
/// arrives as text and a bare Esc byte is taken for the start of an encoded record, so both must
/// be sent as encoded key presses. Every other client, and Codex before it enables the mode,
/// receives plain VT bytes.
/// </remarks>
public static class AgentKeyEncoder
{
	/// <summary>Whether keys for this client must be encoded as win32 key records.</summary>
	public static bool UsesWin32Keys(AgentKind kind, bool win32InputModeActive) =>
		win32InputModeActive && kind == AgentKind.Codex;

	/// <summary>The Enter key press that submits the composer.</summary>
	public static string Enter(AgentKind kind, bool win32InputModeActive) =>
		UsesWin32Keys(kind, win32InputModeActive) ? Win32InputEncoder.EnterKey : "\r";

	/// <summary>The Esc key press.</summary>
	public static string Escape(AgentKind kind, bool win32InputModeActive) =>
		UsesWin32Keys(kind, win32InputModeActive) ? Win32InputEncoder.EscapeKey : "\u001b";

	/// <summary>
	/// Rewrites one typed terminal input for the client: under win32 keys a newline becomes
	/// Shift+Enter, Esc and Ctrl+letter become encoded key presses.
	/// </summary>
	public static string RewriteTyped(string input, AgentKind kind, bool win32InputModeActive)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (!UsesWin32Keys(kind, win32InputModeActive))
		{
			return input;
		}

		if (string.Equals(input, "\n", StringComparison.Ordinal))
		{
			return Win32InputEncoder.ShiftEnter;
		}

		if (input.Length == 1 && input[0] == (char)0x1b)
		{
			return Win32InputEncoder.EscapeKey;
		}

		// Tab, Enter and Backspace share bytes with Ctrl+I, Ctrl+M and Ctrl+H but mean their own
		// keys, which the client already receives correctly.
		if (input.Length == 1
			&& input[0] is >= (char)0x01 and <= (char)0x1A
			&& input[0] is not ('\t' or '\r' or '\b'))
		{
			return Win32InputEncoder.CtrlLetter(input[0]);
		}

		return input;
	}

	/// <summary>
	/// Whether written input contains an Enter key press, in either encoding. Shift+Enter inserts
	/// a newline and is not a submit.
	/// </summary>
	public static bool IsSubmit(string written)
	{
		ArgumentNullException.ThrowIfNull(written);
		return written.Contains('\r', StringComparison.Ordinal)
			|| written.Contains(Win32InputEncoder.EnterKey, StringComparison.Ordinal);
	}
}

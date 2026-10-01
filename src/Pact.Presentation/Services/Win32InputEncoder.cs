namespace Pact.Presentation.Services;

/// <summary>
/// Encodes keystrokes in ConPTY's win32-input-mode wire format
/// (CSI Vk;Sc;Uc;Kd;Cs;Rc _). When a client TUI enables the mode
/// (ESC[?9001h), ConPTY expects the terminal to send keyboard input as
/// encoded Win32 KEY_EVENT records; plain VT bytes lose modifier
/// information (Shift+Enter and Enter both arrive as '\r').
/// </summary>
public static class Win32InputEncoder
{
	private const char Escape = (char)0x1b;

	// VK_RETURN=13, scan code 28, unicode char '\r' (13),
	// SHIFT_PRESSED=0x0010 (16), repeat count 1; keydown then keyup.

	/// <summary>
	/// Shift+Enter as a key-event pair, used to insert a newline without submitting. A bare
	/// <c>\n</c> loses the modifier under win32-input-mode and submits instead.
	/// </summary>
	public static readonly string ShiftEnter =
		$"{Escape}[13;28;13;1;16;1_{Escape}[13;28;13;0;16;1_";

	// VK_RETURN=13, scan code 28, unicode char '\r' (13), no modifiers.
	// Used when host code needs to submit a command while win32-input-mode is
	// active; a raw '\r' can be interpreted as text input instead of a key.
	/// <summary>
	/// Enter as a key-event pair, for submitting while the mode is active. A raw <c>\r</c> can
	/// be taken for text input rather than a key press.
	/// </summary>
	public static readonly string EnterKey =
		$"{Escape}[13;28;13;1;0;1_{Escape}[13;28;13;0;0;1_";

	// VK_ESCAPE=27, scan code 1, unicode char ESC (27), no modifiers.
	// A bare VT ESC byte is swallowed by ConPTY's win32-input-mode parser
	// (taken for the start of an encoded record), so the Esc key must be
	// sent as a real KEY_EVENT pair while the mode is active.
	/// <summary>
	/// Esc as a key-event pair. A bare VT escape byte is swallowed by the win32-input-mode
	/// parser, which mistakes it for the start of an encoded record.
	/// </summary>
	public static readonly string EscapeKey =
		$"{Escape}[27;1;27;1;0;1_{Escape}[27;1;27;0;0;1_";

	// VK_C=67 (0x43), scan code 46 (0x2E), unicode char ETX (3),
	// LEFT_CTRL_PRESSED=8 (0x0008), repeat count 1; keydown then keyup.
	// A bare VT ETX byte ('\x03') is not surfaced as a Ctrl+C key press while
	// win32-input-mode is active, so the interrupt never reaches the client
	// TUI (codex): it must be sent as a real KEY_EVENT pair.
	/// <summary>
	/// Ctrl+C as a key-event pair, so the interrupt actually reaches the client TUI. A bare
	/// <c>\x03</c> is not surfaced as a key press while the mode is active.
	/// </summary>
	public static readonly string CtrlC =
		$"{Escape}[67;46;3;1;8;1_{Escape}[67;46;3;0;8;1_";

	// Set-1 scan codes of the letter keys A..Z, in alphabetical order.
	private static readonly int[] LetterScanCodes =
	[
		30, 48, 46, 32, 18, 33, 34, 35, 23, 36, 37, 38, 50,
		49, 24, 25, 16, 19, 31, 20, 22, 47, 17, 45, 21, 44
	];

	/// <summary>
	/// Encodes the control character a terminal sends for Ctrl+letter (0x01 for Ctrl+A through
	/// 0x1A for Ctrl+Z) as a key-event pair with the letter's virtual key and Ctrl held. A bare
	/// control byte reaches the client as a character without the Ctrl modifier, so editing
	/// shortcuts such as Ctrl+U never fire.
	/// </summary>
	public static string CtrlLetter(char control)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(control, (char)0x01);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(control, (char)0x1A);
		var virtualKey = 'A' + control - 1;
		var scanCode = LetterScanCodes[control - 1];
		var code = (int)control;
		return $"{Escape}[{virtualKey};{scanCode};{code};1;8;1_{Escape}[{virtualKey};{scanCode};{code};0;8;1_";
	}
}
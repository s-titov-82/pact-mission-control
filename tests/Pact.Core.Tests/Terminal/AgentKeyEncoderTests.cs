using Pact.Core.Agents;
using Pact.Core.Terminal;

namespace Pact.Core.Tests.Terminal;

public sealed class AgentKeyEncoderTests
{
	[TestCase('v', 86, 47)]
	[TestCase('u', 85, 22)]
	[TestCase('c', 67, 46)]
	public void Codex_control_shortcuts_preserve_the_terminal_letter(char letter, int virtualKey, int scanCode)
	{
		var control = ((char)(letter - 'a' + 1)).ToString();
		var written = AgentKeyEncoder.RewriteTyped(control, AgentKind.Codex, win32InputModeActive: true);
		written.ShouldBe($"\u001b[{virtualKey};{scanCode};{(int)letter};1;8;1_\u001b[{virtualKey};{scanCode};{(int)letter};0;8;1_");
	}

	[Test]
	public void Codex_in_win32_mode_gets_encoded_enter_and_escape()
	{
		AgentKeyEncoder.Enter(AgentKind.Codex, win32InputModeActive: true).ShouldBe(Win32InputEncoder.EnterKey);
		AgentKeyEncoder.Escape(AgentKind.Codex, win32InputModeActive: true).ShouldBe(Win32InputEncoder.EscapeKey);
	}

	[TestCase(AgentKind.Codex, false)]
	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Hermes, true)]
	public void Other_clients_get_plain_enter_and_escape(AgentKind kind, bool win32InputModeActive)
	{
		AgentKeyEncoder.Enter(kind, win32InputModeActive).ShouldBe("\r");
		AgentKeyEncoder.Escape(kind, win32InputModeActive).ShouldBe("\u001b");
	}

	[Test]
	public void Both_enter_encodings_are_a_submit()
	{
		AgentKeyEncoder.IsSubmit("\r").ShouldBeTrue();
		AgentKeyEncoder.IsSubmit(Win32InputEncoder.EnterKey).ShouldBeTrue();
	}

	[Test]
	public void Shift_enter_and_text_are_not_a_submit()
	{
		AgentKeyEncoder.IsSubmit(Win32InputEncoder.ShiftEnter).ShouldBeFalse();
		AgentKeyEncoder.IsSubmit("\u001b[200~line one\nline two\u001b[201~").ShouldBeFalse();
	}
}

using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class Win32InputTests
{
	private static readonly string Esc = ((char)0x1b).ToString();

	[Test]
	public void ShiftEnter_encodes_keydown_and_keyup_records() =>
		// win32-input-mode (CSI Vk;Sc;Uc;Kd;Cs;Rc _): VK_RETURN=13, scan=28,
		// char='\r'(13), SHIFT_PRESSED=0x0010, repeat=1.
		Win32InputEncoder.ShiftEnter.ShouldBe($"{Esc}[13;28;13;1;16;1_{Esc}[13;28;13;0;16;1_");

	[Test]
	public void Enter_encodes_keydown_and_keyup_records() =>
		// win32-input-mode (CSI Vk;Sc;Uc;Kd;Cs;Rc _): VK_RETURN=13, scan=28,
		// char='\r'(13), no modifiers, repeat=1.
		Win32InputEncoder.EnterKey.ShouldBe($"{Esc}[13;28;13;1;0;1_{Esc}[13;28;13;0;0;1_");

	[Test]
	public void Escape_encodes_keydown_and_keyup_records() =>
		// VK_ESCAPE=27, scan=1, char=ESC(27), no modifiers, repeat=1.
		Win32InputEncoder.EscapeKey.ShouldBe($"{Esc}[27;1;27;1;0;1_{Esc}[27;1;27;0;0;1_");
}

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

	[Test]
	public void Tracker_is_inactive_by_default()
	{
		Win32InputModeTracker tracker = new();

		tracker.IsActive.ShouldBeFalse();
	}

	[Test]
	public void Tracker_activates_on_9001h()
	{
		Win32InputModeTracker tracker = new();

		tracker.Scan($"output {Esc}[?9001h more");

		tracker.IsActive.ShouldBeTrue();
	}

	[Test]
	public void Tracker_deactivates_on_9001l()
	{
		Win32InputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001h");
		tracker.Scan($"{Esc}[?9001l");

		tracker.IsActive.ShouldBeFalse();
	}

	[Test]
	public void Tracker_uses_last_toggle_when_chunk_contains_both()
	{
		Win32InputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001l noise {Esc}[?9001h");

		tracker.IsActive.ShouldBeTrue();
	}

	[Test]
	public void Tracker_ignores_unrelated_output()
	{
		Win32InputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001h");
		tracker.Scan($"plain text {Esc}[31mred{Esc}[0m {Esc}[?1004h");

		tracker.IsActive.ShouldBeTrue();
	}
}
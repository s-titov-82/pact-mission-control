using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ClientInputModeTrackerTests
{
	private static readonly string Esc = ((char)0x1b).ToString();

	[Test]
	public void Tracker_is_inactive_by_default()
	{
		ClientInputModeTracker tracker = new();

		tracker.IsWin32InputActive.ShouldBeFalse();
		tracker.IsSgrMouseActive.ShouldBeFalse();
	}

	[Test]
	public void Win32_input_activates_on_9001h()
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan($"output {Esc}[?9001h more");

		tracker.IsWin32InputActive.ShouldBeTrue();
	}

	[Test]
	public void Win32_input_deactivates_on_9001l()
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001h");
		tracker.Scan($"{Esc}[?9001l");

		tracker.IsWin32InputActive.ShouldBeFalse();
	}

	[Test]
	public void Win32_input_uses_last_toggle_when_chunk_contains_both()
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001l noise {Esc}[?9001h");

		tracker.IsWin32InputActive.ShouldBeTrue();
	}

	[Test]
	public void Modes_survive_unrelated_output()
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?9001h{Esc}[?1003;1006h");
		tracker.Scan($"plain text {Esc}[31mred{Esc}[0m {Esc}[?1004h{Esc}[?25l{Esc}[?2026h{Esc}[?2026l");

		tracker.IsWin32InputActive.ShouldBeTrue();
		tracker.IsSgrMouseActive.ShouldBeTrue();
	}

	[TestCase("\u001b[?1003;1006h", true)]
	[TestCase("\u001b[?1000h\u001b[?1006h", true)]
	[TestCase("\u001b[?1003h", false)]
	[TestCase("\u001b[?1006h", false)]
	[TestCase("\u001b[?1003;1006h\u001b[?1003;1006l", false)]
	[TestCase("\u001b[?1003;1006l\u001b[?1002;1006h", true)]
	[TestCase("\u001b[?1000h\u001b[?1003h\u001b[?1000l\u001b[?1006h", true)]
	[TestCase("\u001b[?1000;1003;1006h\u001b[?1000;1003l", false)]
	public void Sgr_mouse_follows_the_last_tracking_and_encoding_modes(string output, bool expected)
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan(output);

		tracker.IsSgrMouseActive.ShouldBe(expected);
	}

	[Test]
	public void Mouse_modes_do_not_change_win32_input()
	{
		ClientInputModeTracker tracker = new();

		tracker.Scan($"{Esc}[?1003;1006h");

		tracker.IsWin32InputActive.ShouldBeFalse();
	}
}

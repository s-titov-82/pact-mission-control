using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class TerminalDisplayOutputFilterTests
{
	private const string Esc = "";

	[Test]
	public void Filter_preserves_alternate_screen_enter_and_exit()
	{
		TerminalDisplayOutputFilter filter = new();

		var input = $"before {Esc}[?1049hinside{Esc}[?1049l after";

		filter.Filter(input).ShouldBe(input);
	}

	[Test]
	public void Filter_preserves_mouse_tracking_sequences()
	{
		TerminalDisplayOutputFilter filter = new();

		var input =
			$"before {Esc}[?1000h{Esc}[?1002h{Esc}[?1003h{Esc}[?1006hinside{Esc}[?1000l after";

		filter.Filter(input).ShouldBe(input);
	}

	[Test]
	public void Filter_preserves_mouse_tracking_sequence_split_across_chunks()
	{
		TerminalDisplayOutputFilter filter = new();

		var first = filter.Filter($"before {Esc}[?100");
		var second = filter.Filter("0hinside");

		// The trailing incomplete escape is carried to the next chunk, then
		// emitted intact so xterm can enter mouse-reporting mode.
		first.ShouldBe("before ");
		second.ShouldBe($"{Esc}[?1000hinside");
	}

	[Test]
	public void Filter_strips_clear_scrollback_sequence()
	{
		TerminalDisplayOutputFilter filter = new();

		var text = filter.Filter($"before {Esc}[3Jafter");

		text.ShouldBe("before after");
	}

	[Test]
	public void Filter_strips_clear_scrollback_sequence_split_across_chunks()
	{
		TerminalDisplayOutputFilter filter = new();

		var first = filter.Filter($"before {Esc}[3");
		var second = filter.Filter("Jafter");

		first.ShouldBe("before ");
		second.ShouldBe("after");
	}

	[Test]
	public void Filter_strips_full_terminal_reset_sequence()
	{
		TerminalDisplayOutputFilter filter = new();

		var text = filter.Filter($"before {Esc}cafter");

		text.ShouldBe("before after");
	}

	[Test]
	public void Filter_preserves_regular_ansi_sequences()
	{
		TerminalDisplayOutputFilter filter = new();

		var input = $"before {Esc}[31mred{Esc}[0m after";

		filter.Filter(input).ShouldBe(input);
	}
}
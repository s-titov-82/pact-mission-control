using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class TerminalSizeBookTests
{
	[Test]
	public void A_new_terminal_starts_at_the_default_size_before_any_terminal_was_fitted()
	{
		TerminalSizeBook sizes = new((120, 36));

		sizes.Reserve("session-1").ShouldBe((120, 36));
		sizes.Get("session-1").ShouldBe((120, 36));
	}

	[Test]
	public void A_new_terminal_starts_at_the_size_last_fitted_for_any_terminal()
	{
		TerminalSizeBook sizes = new((120, 36));
		sizes.Record("shown", 101, 37);

		// The process starts from this size, so it is known before the page reports anything.
		sizes.Reserve("hidden").ShouldBe((101, 37));
		sizes.Get("hidden").ShouldBe((101, 37));
	}

	[Test]
	public void A_forgotten_terminal_falls_back_to_the_default_size()
	{
		TerminalSizeBook sizes = new((120, 36));
		sizes.Record("session-1", 90, 30);

		sizes.Remove("session-1");

		sizes.Get("session-1").ShouldBe((120, 36));
	}
}

using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class PausedPlaceholderTextTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

	[Test]
	public void A_paused_session_names_its_kind_and_directory()
	{
		SessionViewModel session = new(new SessionRecord(
			"session-1",
			AgentKind.Codex,
			"Codex",
			@"C:\work\repo",
			"codex",
			null,
			SessionStatus.Stopped,
			Now,
			Now));

		PausedPlaceholderText.For(new PresentedSurface.PausedSession(session))
			.ShouldBe(@"Paused — Codex, C:\work\repo");
	}

	[Test]
	public void A_paused_web_page_names_its_address()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1",
			"Wiki",
			"https://example.test/wiki",
			"https://example.test/wiki",
			Now,
			Now));

		PausedPlaceholderText.For(new PresentedSurface.PausedWebPage(page))
			.ShouldBe("Paused — https://example.test/wiki");
	}

	[Test]
	public void Anything_but_a_paused_item_has_no_caption()
	{
		PausedPlaceholderText.For(new PresentedSurface.None()).ShouldBeEmpty();
	}
}

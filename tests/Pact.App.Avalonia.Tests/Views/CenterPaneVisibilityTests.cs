using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class CenterPaneVisibilityTests
{
	private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
	private static readonly SessionViewModel Session = new(new SessionRecord(
		"session-1",
		AgentKind.Codex,
		"Codex",
		Path.GetTempPath(),
		"codex",
		null,
		SessionStatus.Stopped,
		Now,
		Now));
	private static readonly WebPageViewModel WebPage = new(new WebPageRecord(
		"web-1",
		"Docs",
		"https://example.test",
		"https://example.test",
		Now,
		Now));

	// The mapping reads only the case, so the heavier view models are left null.
	private static IEnumerable<TestCaseData> Rows()
	{
		yield return Row(new PresentedSurface.Terminal(Session, Loading: false, FocusWhenLoaded: false), false, Expect(terminal: true)).SetName("Terminal");
		yield return Row(new PresentedSurface.Terminal(Session, Loading: true, FocusWhenLoaded: true), false, Expect(terminalLoading: true)).SetName("Terminal loading");
		yield return Row(new PresentedSurface.PausedSession(Session), false, Expect(paused: true)).SetName("Paused session");
		yield return Row(new PresentedSurface.PausedWebPage(WebPage), true, Expect(paused: true)).SetName("Paused web page");
		yield return Row(new PresentedSurface.Notes(null!, null!, null!), false, Expect(notes: true)).SetName("Notes");
		yield return Row(new PresentedSurface.ScenarioRun(null!), false, Expect(journal: true)).SetName("Scenario run");
		yield return Row(new PresentedSurface.Web(), true, Expect(browser: true)).SetName("Web with a page");
		yield return Row(new PresentedSurface.Web(), false, Expect(empty: true)).SetName("Web without a page");
		yield return Row(new PresentedSurface.None(), true, Expect(browser: true)).SetName("None with a page");
		yield return Row(new PresentedSurface.None(), false, Expect(empty: true)).SetName("None without a page");
	}

	[TestCaseSource(nameof(Rows))]
	public void Each_surface_shows_exactly_its_pane(
		object surface,
		bool hasSelectedWebPage,
		object expected)
	{
		// The types are internal, so the public test signature takes them as objects.
		CenterPaneVisibility.For((PresentedSurface)surface, hasSelectedWebPage)
			.ShouldBe((CenterPaneVisibility)expected);
	}

	private static TestCaseData Row(PresentedSurface surface, bool hasSelectedWebPage, CenterPaneVisibility expected) =>
		new(surface, hasSelectedWebPage, expected);

	private static CenterPaneVisibility Expect(
		bool terminal = false,
		bool terminalLoading = false,
		bool paused = false,
		bool notes = false,
		bool journal = false,
		bool browser = false,
		bool empty = false) =>
		new(terminal, terminalLoading, paused, notes, journal, browser, empty);
}

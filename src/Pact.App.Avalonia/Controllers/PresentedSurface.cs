using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// What the center pane shows. The shell assigns it whole, so showing one thing drops
/// everything that belonged to the previous one, including a terminal's loading state.
/// </summary>
internal abstract record PresentedSurface
{
	/// <summary>A session's terminal, possibly still behind its loading surface.</summary>
	/// <param name="Session">The session whose terminal is shown.</param>
	/// <param name="Loading">The terminal is starting and hidden behind the loading surface.</param>
	/// <param name="FocusWhenLoaded">Focus was asked for while loading; apply it when loading clears.</param>
	public sealed record Terminal(SessionViewModel Session, bool Loading, bool FocusWhenLoaded) : PresentedSurface;

	/// <summary>The paused placeholder for a ROOT session.</summary>
	public sealed record PausedSession(SessionViewModel Session) : PresentedSurface;

	/// <summary>The paused placeholder for a ROOT web page.</summary>
	public sealed record PausedWebPage(WebPageViewModel WebPage) : PresentedSurface;

	/// <summary>A project's screen: overview, changes and review logs.</summary>
	public sealed record Project(ProjectScreenViewModel Screen) : PresentedSurface;

	/// <summary>The browser, showing the view model's selected web page.</summary>
	public sealed record Web : PresentedSurface;

	/// <summary>Nothing chosen by the shell; the window falls back to the selected web page or the empty pane.</summary>
	public sealed record None : PresentedSurface;
}

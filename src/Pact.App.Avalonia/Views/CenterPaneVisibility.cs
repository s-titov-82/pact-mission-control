using Pact.App.Avalonia.Controllers;

namespace Pact.App.Avalonia.Views;

/// <summary>Which center-pane surfaces are visible for a <see cref="PresentedSurface"/>.</summary>
internal readonly record struct CenterPaneVisibility(
	bool Terminal,
	bool TerminalLoading,
	bool Paused,
	bool Browser,
	bool Empty,
	bool Project = false,
	bool Dashboard = false)
{
	/// <summary>
	/// Maps the shown surface to exactly one visible pane. Without a surface of its own, the
	/// view model's selected web page still shows in the browser, since the view model selects
	/// a page by itself in some paths.
	/// </summary>
	public static CenterPaneVisibility For(PresentedSurface surface, bool hasSelectedWebPage)
	{
		ArgumentNullException.ThrowIfNull(surface);
		return surface switch
		{
			PresentedSurface.Dashboard => new(false, false, false, false, false, false, true),
			PresentedSurface.Terminal { Loading: false } => new(true, false, false, false, false),
			PresentedSurface.Terminal => new(false, true, false, false, false),
			PresentedSurface.PausedSession or PresentedSurface.PausedWebPage => new(false, false, true, false, false),
			PresentedSurface.Project => new(false, false, false, false, false, true),
			_ => new(false, false, false, hasSelectedWebPage, !hasSelectedWebPage)
		};
	}
}

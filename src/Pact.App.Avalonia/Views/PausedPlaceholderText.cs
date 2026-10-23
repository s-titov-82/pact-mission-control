using Pact.App.Avalonia.Controllers;

namespace Pact.App.Avalonia.Views;

/// <summary>The caption under the logo on the paused placeholder: what is paused.</summary>
internal static class PausedPlaceholderText
{
	public static string For(PresentedSurface surface) => surface switch
	{
		PresentedSurface.PausedSession paused =>
			$"Paused — {paused.Session.Record.Kind}, {paused.Session.Record.WorkingDirectory}",
		PresentedSurface.PausedWebPage paused => $"Paused — {paused.WebPage.ResumeUrl}",
		_ => string.Empty
	};
}

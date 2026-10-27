using Avalonia.Input;
using Avalonia.Interactivity;
using Markdown.Avalonia.Full;

namespace Pact.App.Avalonia.Views;

/// <summary>
/// Keeps clicks away from Markdown.Avalonia's click-to-select while the viewer shows nothing: once
/// a rendered document turns empty, that code indexes a block that is gone and the exception
/// leaves the message loop, freezing the window.
/// </summary>
internal static class MarkdownClickGuard
{
	public static void Attach(MarkdownScrollViewer viewer)
	{
		ArgumentNullException.ThrowIfNull(viewer);
		viewer.AddHandler(
			InputElement.PointerPressedEvent,
			(_, e) =>
			{
				if (string.IsNullOrWhiteSpace(viewer.Markdown))
				{
					e.Handled = true;
				}
			},
			RoutingStrategies.Tunnel);
	}
}

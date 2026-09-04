namespace Pact.App.Avalonia.Views;

/// <summary>
/// The one braille spinner shared by every activity indicator, so terminal tabs, web pages, and
/// the git panel animate identically.
/// </summary>
internal static class BusySpinner
{
	/// <summary>Tick interval the owning view should use to advance frames.</summary>
	internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

	private static readonly string[] Frames =
		["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	/// <summary>Returns the frame at <paramref name="frameIndex"/> and advances it.</summary>
	internal static string Advance(ref int frameIndex)
	{
		var frame = Frames[frameIndex % Frames.Length];
		frameIndex = (frameIndex + 1) % Frames.Length;
		return frame;
	}
}

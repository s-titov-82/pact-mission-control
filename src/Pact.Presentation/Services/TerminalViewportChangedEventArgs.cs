namespace Pact.Presentation.Services;

/// <summary>
/// Reports a terminal's new cell dimensions after a resize.
/// </summary>
/// <param name="columns">New width in cells.</param>
/// <param name="rows">New height in cells.</param>
public sealed class TerminalViewportChangedEventArgs(int columns, int rows) : EventArgs
{
	/// <summary>Viewport width in cells.</summary>
	public int Columns { get; } = columns;

	/// <summary>Viewport height in cells.</summary>
	public int Rows { get; } = rows;
}
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Diff;

/// <summary>Tints added, removed and hunk-header lines across the editor's width.</summary>
internal sealed class DiffLineBackgroundRenderer : IBackgroundRenderer
{
	private static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#2E16A34A"));
	private static readonly IBrush RemovedBrush = new SolidColorBrush(Color.Parse("#2EDC2626"));
	private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#1E3B82F6"));

	private IReadOnlyList<DiffRowViewModel> _rows = [];

	/// <inheritdoc />
	public KnownLayer Layer => KnownLayer.Background;

	/// <summary>Takes the rows whose kinds the document's lines have, in order.</summary>
	public void SetRows(IReadOnlyList<DiffRowViewModel> rows) => _rows = rows;

	/// <summary>The kind of a 1-based document line.</summary>
	public DiffRowKind KindOf(int lineNumber) =>
		lineNumber >= 1 && lineNumber <= _rows.Count ? _rows[lineNumber - 1].Kind : DiffRowKind.Context;

	/// <inheritdoc />
	public void Draw(TextView textView, DrawingContext drawingContext)
	{
		foreach (var line in textView.VisualLines)
		{
			var brush = KindOf(line.FirstDocumentLine.LineNumber) switch
			{
				DiffRowKind.Added => AddedBrush,
				DiffRowKind.Removed => RemovedBrush,
				DiffRowKind.HunkHeader => HeaderBrush,
				_ => null
			};
			if (brush is not null)
			{
				drawingContext.FillRectangle(
					brush,
					new Rect(0, line.VisualTop - textView.VerticalOffset, textView.Bounds.Width, line.Height));
			}
		}
	}
}

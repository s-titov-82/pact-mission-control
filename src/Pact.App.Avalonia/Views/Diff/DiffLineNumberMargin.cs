using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Diff;

/// <summary>Old and new line numbers of each diff line, in two columns.</summary>
internal sealed class DiffLineNumberMargin : AbstractMargin
{
	private const double ColumnGap = 10;
	private IReadOnlyList<DiffRowViewModel> _rows = [];
	private int _digits = 1;

	/// <summary>Raised with the row index (document line − 1) of a click on a line number.</summary>
	public event EventHandler<int>? RowClicked;

	/// <inheritdoc />
	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (TextView is { VisualLinesValid: true } textView
			&& textView.GetVisualLineFromVisualTop(e.GetPosition(this).Y + textView.VerticalOffset) is { } line)
		{
			e.Handled = true;
			RowClicked?.Invoke(this, line.FirstDocumentLine.LineNumber - 1);
		}
	}

	/// <summary>Takes the rows whose numbers the document's lines have, in order.</summary>
	public void SetRows(IReadOnlyList<DiffRowViewModel> rows)
	{
		_rows = rows;
		_digits = Math.Max(1, rows.Select(row => Math.Max(row.OldNumber.Length, row.NewNumber.Length)).DefaultIfEmpty(1).Max());
		InvalidateMeasure();
		InvalidateVisual();
	}

	/// <summary>The old and new numbers of a 1-based document line, empty where it has none.</summary>
	public (string Old, string New) NumbersOf(int lineNumber) =>
		lineNumber >= 1 && lineNumber <= _rows.Count
			? (_rows[lineNumber - 1].OldNumber, _rows[lineNumber - 1].NewNumber)
			: (string.Empty, string.Empty);

	/// <inheritdoc />
	protected override Size MeasureOverride(Size availableSize) =>
		new(2 * (ColumnWidth() + ColumnGap) + ColumnGap, 0);

	/// <inheritdoc />
	public override void Render(DrawingContext context)
	{
		var textView = TextView;
		if (textView is not { VisualLinesValid: true })
		{
			return;
		}

		// A transparent fill makes the whole column take clicks, not only the digits.
		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		var column = ColumnWidth();
		var brush = this.TryFindResource("AppTextMutedBrush", ActualThemeVariant, out var found) && found is IBrush muted
			? muted
			: Brushes.Gray;
		foreach (var line in textView.VisualLines)
		{
			var (oldNumber, newNumber) = NumbersOf(line.FirstDocumentLine.LineNumber);
			var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
			DrawRightAligned(context, oldNumber, ColumnGap + column, top, brush);
			DrawRightAligned(context, newNumber, 2 * (ColumnGap + column), top, brush);
		}
	}

	/// <inheritdoc />
	protected override void OnTextViewChanged(TextView? oldTextView, TextView? newTextView)
	{
		oldTextView?.VisualLinesChanged -= OnVisualLinesChanged;
		base.OnTextViewChanged(oldTextView, newTextView);
		newTextView?.VisualLinesChanged += OnVisualLinesChanged;
		InvalidateVisual();
	}

	private void OnVisualLinesChanged(object? sender, EventArgs e) => InvalidateVisual();

	private void DrawRightAligned(DrawingContext context, string text, double right, double top, IBrush brush)
	{
		if (text.Length == 0)
		{
			return;
		}

		var formatted = Format(text, brush);
		context.DrawText(formatted, new Point(right - formatted.Width, top));
	}

	private double ColumnWidth() => Format(new string('9', _digits), Brushes.Gray).Width;

	private FormattedText Format(string text, IBrush brush) => new(
		text,
		CultureInfo.InvariantCulture,
		FlowDirection.LeftToRight,
		new Typeface(GetValue(TextElement.FontFamilyProperty)),
		GetValue(TextElement.FontSizeProperty),
		brush);
}

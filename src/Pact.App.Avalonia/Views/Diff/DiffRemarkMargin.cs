using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Pact.Core.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Diff;

/// <summary>
/// The remark column of the diff: a marker on rows with threads, coloured by the most pressing
/// state, and a "+" on the hovered row that takes a remark. A click opens the row's threads.
/// </summary>
internal sealed class DiffRemarkMargin : AbstractMargin
{
	private const double ColumnWidth = 22;
	private static readonly IBrush Uncertain = new SolidColorBrush(Color.Parse("#F59E0B"));
	private static readonly IBrush Sent = new SolidColorBrush(Color.Parse("#3B82F6"));
	private static readonly IBrush Answered = new SolidColorBrush(Color.Parse("#16A34A"));
	private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#80808080"));
	private static readonly IBrush PlusBadge = new SolidColorBrush(Color.Parse("#3B82F6"));

	private Func<int, bool> _canRemark = static _ => false;
	private IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> _threads = new Dictionary<int, IReadOnlyList<RemarkThreadViewModel>>();
	private int? _hoverRow;

	/// <summary>Raised with the row index (document line − 1) of a click on a marker or a "+".</summary>
	public event EventHandler<int>? RowClicked;

	/// <summary>Takes which rows take a remark and the threads on rows.</summary>
	public void SetMarks(Func<int, bool> canRemark, IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> threads)
	{
		_canRemark = canRemark ?? throw new ArgumentNullException(nameof(canRemark));
		_threads = threads ?? throw new ArgumentNullException(nameof(threads));
		InvalidateVisual();
	}

	/// <summary>Shows the "+" on a row hovered anywhere on the line, not only over this column.</summary>
	public void SetHoverRow(int? row) => SetHover(row);

	/// <summary>What a 1-based document line shows: <c>thread</c>, <c>+</c> or nothing.</summary>
	public string MarkOf(int line)
	{
		var row = line - 1;
		if (_threads.ContainsKey(row))
		{
			return "thread";
		}

		return _hoverRow == row && _canRemark(row) ? "+" : string.Empty;
	}

	/// <inheritdoc />
	protected override Size MeasureOverride(Size availableSize) => new(ColumnWidth, 0);

	/// <inheritdoc />
	public override void Render(DrawingContext context)
	{
		var textView = TextView;
		if (textView is not { VisualLinesValid: true })
		{
			return;
		}

		context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
		var muted = this.TryFindResource("AppTextMutedBrush", ActualThemeVariant, out var found) && found is IBrush brush
			? brush
			: Brushes.Gray;
		foreach (var line in textView.VisualLines)
		{
			var row = line.FirstDocumentLine.LineNumber - 1;
			var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - textView.VerticalOffset;
			var height = line.Height;
			if (_threads.TryGetValue(row, out var threads))
			{
				var size = Math.Min(8, height - 4);
				context.DrawEllipse(BrushOf(threads, muted), null, new Point(ColumnWidth / 2, top + (height / 2)), size / 2, size / 2);
			}
			else if (_hoverRow == row && _canRemark(row))
			{
				// A filled badge, so the way to add a remark is seen, not guessed.
				var size = Math.Min(16, height - 2);
				var badge = new Rect((ColumnWidth - size) / 2, top + ((height - size) / 2), size, size);
				context.DrawRectangle(PlusBadge, null, badge, 4, 4);
				FormattedText plus = new(
					"+",
					CultureInfo.InvariantCulture,
					FlowDirection.LeftToRight,
					new Typeface(GetValue(TextElement.FontFamilyProperty), weight: FontWeight.Bold),
					size - 2,
					Brushes.White);
				context.DrawText(plus, new Point(badge.X + ((size - plus.Width) / 2), badge.Y + ((size - plus.Height) / 2)));
			}
		}
	}

	/// <inheritdoc />
	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		SetHover(RowAt(e.GetPosition(this).Y));
	}

	/// <inheritdoc />
	protected override void OnPointerExited(PointerEventArgs e)
	{
		base.OnPointerExited(e);
		SetHover(null);
	}

	/// <inheritdoc />
	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		if (RowAt(e.GetPosition(this).Y) is { } row && (_threads.ContainsKey(row) || _canRemark(row)))
		{
			e.Handled = true;
			RowClicked?.Invoke(this, row);
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

	private int? RowAt(double y)
	{
		if (TextView is not { VisualLinesValid: true } textView)
		{
			return null;
		}

		var line = textView.GetVisualLineFromVisualTop(y + textView.VerticalOffset);
		return line is null ? null : line.FirstDocumentLine.LineNumber - 1;
	}

	private void SetHover(int? row)
	{
		if (_hoverRow != row)
		{
			_hoverRow = row;
			InvalidateVisual();
		}
	}

	// The most pressing state of the row wins: something to act on before something settled.
	private static IBrush BrushOf(IReadOnlyList<RemarkThreadViewModel> threads, IBrush muted)
	{
		var states = threads.Select(thread => thread.State).ToArray();
		if (states.Contains(RemarkThreadState.DeliveryUncertain))
		{
			return Uncertain;
		}

		if (states.Contains(RemarkThreadState.Draft))
		{
			return muted;
		}

		if (states.Contains(RemarkThreadState.Sent))
		{
			return Sent;
		}

		return states.Contains(RemarkThreadState.Answered) ? Answered : Faint;
	}
}

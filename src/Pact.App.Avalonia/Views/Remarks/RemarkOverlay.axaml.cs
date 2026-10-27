using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Remarks;

/// <summary>The threads of one diff row, lying over the diff under that row, and the box for a new remark.</summary>
internal sealed partial class RemarkOverlay : UserControl
{
	public RemarkOverlay()
	{
		InitializeComponent();
		AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
	}

	/// <summary>Raised by ✕ and Esc.</summary>
	public event EventHandler? CloseRequested;

	/// <summary>Raised with the text of a new remark.</summary>
	public event EventHandler<string>? AddRequested;

	/// <summary>The threads shown, oldest first.</summary>
	public IReadOnlyList<RemarkThreadViewModel> Threads { get; private set; } = [];

	/// <summary>Shows a row's threads and, when the row takes one, the box for a new remark.</summary>
	public void Show(IReadOnlyList<RemarkThreadViewModel> threads, bool canAdd)
	{
		Threads = threads;
		ThreadsList.ItemsSource = threads;
		ThreadsList.IsVisible = threads.Count > 0;
		NewRemarkPanel.IsVisible = canAdd;
		if (!canAdd)
		{
			NewRemarkBox.Text = string.Empty;
		}
	}

	/// <summary>Empties the new-remark box after its remark was added.</summary>
	public void ClearNewRemark() => NewRemarkBox.Text = string.Empty;

	private void OnAddClicked(object? sender, RoutedEventArgs e)
	{
		if (!string.IsNullOrWhiteSpace(NewRemarkBox.Text))
		{
			AddRequested?.Invoke(this, NewRemarkBox.Text);
		}
	}

	private void OnCloseClicked(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

	private void OnKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape)
		{
			e.Handled = true;
			CloseRequested?.Invoke(this, EventArgs.Empty);
		}
	}
}

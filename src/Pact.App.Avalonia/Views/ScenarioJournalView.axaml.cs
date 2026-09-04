using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class ScenarioJournalView : UserControl
{
	private ScenarioRunViewModel? _run;

	public ScenarioJournalView()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	public event EventHandler<ScenarioRunViewModel>? CloseRunRequested;
	public event EventHandler<ScenarioRunViewModel>? SoftStopRequested;
	public event EventHandler<ScenarioRunViewModel>? PauseRequested;
	public event EventHandler<ScenarioRunViewModel>? AbortRequested;
	public event EventHandler<ScenarioRunViewModel>? ResumeRequested;

	private void OnCloseRunClicked(object? sender, RoutedEventArgs e) => RaiseRunEvent(e, CloseRunRequested);
	private void OnSoftStopClicked(object? sender, RoutedEventArgs e) => RaiseRunEvent(e, SoftStopRequested);
	private void OnPauseClicked(object? sender, RoutedEventArgs e) => RaiseRunEvent(e, PauseRequested);
	private void OnAbortClicked(object? sender, RoutedEventArgs e) => RaiseRunEvent(e, AbortRequested);
	private void OnResumeClicked(object? sender, RoutedEventArgs e) => RaiseRunEvent(e, ResumeRequested);

	private void RaiseRunEvent(RoutedEventArgs e, EventHandler<ScenarioRunViewModel>? handler)
	{
		e.Handled = true;
		if (DataContext is ScenarioRunViewModel run)
		{
			handler?.Invoke(this, run);
		}
	}

	private void OnDataContextChanged(object? sender, EventArgs e)
	{
		if (_run is { } previousRun)
		{
			previousRun.PropertyChanged -= OnRunPropertyChanged;
		}

		_run = DataContext as ScenarioRunViewModel;
		if (_run is { } currentRun)
		{
			currentRun.PropertyChanged += OnRunPropertyChanged;
		}

		RefreshMarkdown(scrollJournalToEnd: true);
	}

	private void OnRunPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ScenarioRunViewModel.JournalMarkdown)
			or nameof(ScenarioRunViewModel.FinalResult))
		{
			RefreshMarkdown(
				scrollJournalToEnd: e.PropertyName == nameof(ScenarioRunViewModel.JournalMarkdown));
		}
	}

	private void RefreshMarkdown(bool scrollJournalToEnd)
	{
		var scroll = ScenarioJournalMarkdownView
			.GetVisualDescendants()
			.OfType<ScrollViewer>()
			.FirstOrDefault();
		var nearBottom = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 24;
		ScenarioJournalMarkdownView.Markdown = _run?.JournalMarkdown ?? string.Empty;
		ScenarioFinalResultMarkdownView.Markdown = _run?.FinalResult ?? string.Empty;
		if (!scrollJournalToEnd || !nearBottom)
		{
			return;
		}

		Dispatcher.UIThread.Post(() =>
		{
			var current = ScenarioJournalMarkdownView
				.GetVisualDescendants()
				.OfType<ScrollViewer>()
				.FirstOrDefault();
			current?.Offset = new Vector(current.Offset.X, current.Extent.Height);
		});
	}
}

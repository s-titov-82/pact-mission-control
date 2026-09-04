using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class GitPanelView : UserControl
{
	private readonly DispatcherTimer _activitySpinnerTimer;
	private ObservedTaskGroup _eventTasks = new(
		static (_, _) => Task.CompletedTask);
	private Func<Exception, Task> _reportUserFailureAsync =
		static _ => Task.CompletedTask;
	private bool _closing;
	private int _activitySpinnerFrameIndex;

	public GitPanelView()
	{
		InitializeComponent();
		_activitySpinnerTimer = new DispatcherTimer { Interval = BusySpinner.Interval };
		_activitySpinnerTimer.Tick += (_, _) => AdvanceActivitySpinnerFrame();
		AttachedToVisualTree += (_, _) => _activitySpinnerTimer.Start();
		DetachedFromVisualTree += (_, _) => _activitySpinnerTimer.Stop();
	}

	/// <summary>
	/// Advances the branch-row activity spinner, which spins for as long as the panel is waiting
	/// for git rather than for as long as its buttons are disabled.
	/// </summary>
	internal string AdvanceActivitySpinnerFrame()
	{
		var frame = BusySpinner.Advance(ref _activitySpinnerFrameIndex);
		if (GitActivityIndicator.IsVisible)
		{
			GitActivityIndicator.Text = frame;
		}

		return frame;
	}

	internal AvaloniaGitActionCoordinator? ActionCoordinator { get; set; }

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		Func<Exception, Task>? reportUserFailureAsync = null)
	{
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_reportUserFailureAsync = reportUserFailureAsync ?? _reportUserFailureAsync;
	}

	internal void DetachEventProducers()
	{
		_closing = true;
		_activitySpinnerTimer.Stop();
	}

	public async Task RefreshAsync()
	{
		if (DataContext is GitPanelViewModel viewModel)
		{
			await RunIgnoringCancellationAsync(() => viewModel.RefreshAsync());
		}
	}

	private void OnPopupButtonClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (DataContext is GitPanelViewModel panel
			&& sender is Control { DataContext: GitPopupButtonViewModel button }
			&& button.IsVisible
			&& ActionCoordinator is { } coordinator)
		{
			RunEvent(
				"git-popup-action",
				() => coordinator.RunPopupButtonAsync(panel, button));
		}
	}

	private void OnResolveClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is GitPanelViewModel panel && ActionCoordinator is { } coordinator)
		{
			RunEvent(
				"git-resolve",
				() => AvaloniaGitActionCoordinator.RunResolveAsync(panel));
		}
	}

	private void OnRebaseOntoBaseClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is GitPanelViewModel panel && ActionCoordinator is { } coordinator)
		{
			RunEvent(
				"git-rebase",
				() => AvaloniaGitActionCoordinator.RunRebaseOntoBaseAsync(panel));
		}
	}

	private void OnAbortRebaseClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is GitPanelViewModel panel && ActionCoordinator is { } coordinator)
		{
			RunEvent(
				"git-abort-rebase",
				() => AvaloniaGitActionCoordinator.RunAbortRebaseAsync(panel));
		}
	}

	private void OnHelperClicked(object? sender, RoutedEventArgs e)
	{
		if (DataContext is GitPanelViewModel panel
			&& sender is Control { DataContext: ResolvedGitHelperAction action }
			&& ActionCoordinator is { } coordinator)
		{
			AvaloniaGitActionCoordinator.LaunchHelper(panel, action);
		}
	}

	private void OnGitLogTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (sender is TextBox textBox)
		{
			textBox.CaretIndex = textBox.Text?.Length ?? 0;
		}
	}

	private static async Task RunIgnoringCancellationAsync(Func<Task> action)
	{
		try
		{ await action(); }
		catch (OperationCanceledException) { }
	}

	private void RunEvent(string operationName, Func<Task> operation)
	{
		if (!_closing)
		{
			_eventTasks.TryRun(operationName, operation, _reportUserFailureAsync);
		}
	}
}

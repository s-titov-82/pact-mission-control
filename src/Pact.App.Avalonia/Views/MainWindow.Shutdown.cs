using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Diagnostics;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	internal void OnClosing(object? sender, WindowClosingEventArgs e)
	{
		if (_closeApproved)
		{
			return;
		}

		e.Cancel = true;
		if (_gracefulShutdownTask is not null)
		{
			return;
		}

		var activeSessions = EngineProbeController.GetActiveSessions();
		if (activeSessions.Count > 0)
		{
			if (_closePromptRunning)
			{
				return;
			}

			_closePromptRunning = true;
			RunUiEvent(
				"confirm-app-close",
				() => ConfirmAppCloseAsync(activeSessions));
			return;
		}

		StartGracefulShutdown();
	}

	private async Task ConfirmAppCloseAsync(IReadOnlyList<SessionViewModel> activeSessions)
	{
		try
		{
			if (!await ConfirmStoppingSessionsAsync(
				"Close Pact",
				"Close Pact?",
				activeSessions))
			{
				return;
			}

			StartGracefulShutdown();
		}
		finally
		{
			_closePromptRunning = false;
		}
	}

	private void StartGracefulShutdown()
	{
		if (_gracefulShutdownTask is not null)
		{
			return;
		}

		if (StartGracefulShutdownOverride is not null)
		{
			_gracefulShutdownTask = Task.CompletedTask;
			StartGracefulShutdownOverride();
			return;
		}

		_gracefulShutdownRunning = true;
		App.Bootstrap.RequestStop();
		BeginShellShutdown();
		_gracefulShutdownTask = WindowShutdownCoordinator.CompleteAsync(
			saveLayoutAsync: SaveWindowLayoutAsync,
			showProgressAsync: () => ShowBusyOverlayAsync("Saving session state...", "Force close"),
			shutdownAsync: App.Bootstrap.ShutdownAsync,
			reportFailureAsync: exception => AppLog.AppendAsync(
				App.Bootstrap.Profile.RootDirectory,
				"Window shutdown completed with errors",
				exception),
			approveAndClose: () =>
			{
				_closeApproved = true;
				Close();
			});
	}

	private void BeginShellShutdown()
	{
		DetachEventProducers();
		EngineProbeController.BeginShutdown();
	}

	private void OnBusyOverlayActionClicked(object? sender, RoutedEventArgs e) => ForceCloseNow();
	private void OnControllerBusyOverlayActionRequested(object? sender, EventArgs e) => ForceCloseNow();

	private void OnControllerStatusMessage(object? sender, string message)
	{
		if (!_gracefulShutdownRunning || string.IsNullOrWhiteSpace(message))
		{
			return;
		}

		RunUiEvent("update-shutdown-progress", () => UpdateShutdownProgressAsync(message));
	}

	private async Task UpdateShutdownProgressAsync(string message)
	{
		if (!_gracefulShutdownRunning)
		{
			return;
		}

		BusyOverlayText.Text = message;
		try
		{
			await EngineProbeController.SetBusyOverlayAsync(message, true, true, "Force close");
		}
		catch (Exception)
		{
			// Shutdown progress is best-effort; cleanup must continue if the WebView is already gone.
		}
	}

	private void ForceCloseNow()
	{
		if (!_gracefulShutdownRunning || _closeApproved)
		{
			return;
		}

		_closeApproved = true;
		Environment.Exit(2);
	}
}

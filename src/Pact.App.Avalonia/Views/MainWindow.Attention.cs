using Avalonia;
using Avalonia.Controls;
using Pact.App.Avalonia.Platform;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	private bool _hasTaskbarCompletionAttention;

	private void WireAttentionEvents()
	{
		EngineProbeController.RefreshWindowFacts = PublishTerminalWindowFacts;
		void OnActivated(object? sender, EventArgs args)
		{
			PublishTerminalWindowFacts();
			UpdateTerminalPresentationVisibility();
			_hasTaskbarCompletionAttention = false;
			UpdateTaskbarAttention();
		}

		void OnDeactivated(object? sender, EventArgs args)
		{
			PublishTerminalWindowFacts();
			UpdateTerminalPresentationVisibility();
			UpdateTaskbarAttention();
		}

		void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
		{
			if (args.Property == IsVisibleProperty || args.Property == WindowStateProperty)
			{
				PublishTerminalWindowFacts();
				UpdateTerminalPresentationVisibility();
			}
		}

		Activated += OnActivated;
		Deactivated += OnDeactivated;
		PropertyChanged += OnWindowPropertyChanged;
		_eventDetachments.Add(() => Activated -= OnActivated);
		_eventDetachments.Add(() => Deactivated -= OnDeactivated);
		_eventDetachments.Add(() => PropertyChanged -= OnWindowPropertyChanged);
		_eventDetachments.Add(() => EngineProbeController.RefreshWindowFacts = null);
	}

	private void PublishTerminalWindowFacts()
	{
		var visible = IsTerminalWindowVisible(IsVisible, WindowState);
		var active = WindowForegroundProbe.IsWindowForeground(this);
		EngineProbeController.SetTerminalWindowFacts(visible, active, DateTimeOffset.UtcNow);
		EngineProbeController.SetWebMonitorWindowFacts(visible, active);
	}

	internal static bool IsTerminalWindowVisible(bool isVisible, WindowState windowState) =>
		isVisible && windowState != WindowState.Minimized;

	private void OnUnreadCompletionsChanged()
	{
		// Unread is projected by the status engine; taskbar attention only mirrors it.
		_hasTaskbarCompletionAttention = TaskbarAttentionPolicy.ShouldSetCompletionAttention(
			EngineProbeController.ViewModel.HasUnreadCompletions,
			wasBusyLongEnough: true,
			WindowForegroundProbe.IsWindowForeground(this));
		UpdateTaskbarAttention();
	}

	private void UpdateTaskbarAttention()
	{
		var shouldFlash = TaskbarAttentionPolicy.ShouldFlashTaskbar(
			EngineProbeController.ViewModel.HasUnreadCompletions,
			_hasTaskbarCompletionAttention,
			WindowForegroundProbe.IsWindowForeground(this));
		if (shouldFlash)
		{
			_userAttention?.RequestAttention();
		}
		else
		{
			_userAttention?.ClearAttention();
		}
	}
}

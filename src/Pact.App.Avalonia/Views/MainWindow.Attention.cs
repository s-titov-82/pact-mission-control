using Avalonia;
using Avalonia.Controls;
using Pact.App.Avalonia.Platform;
using Pact.Presentation.Services;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	private bool _hasTaskbarCompletionAttention;
	private AttentionNotificationList? _notifications;

	private void WireWindowsNotifications(string profileRoot)
	{
		var feed = EngineProbeController.ViewModel.Attention;
		var group = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profileRoot.ToUpperInvariant())))[..16];
		void Report(Exception ex)
		{
			RunUiEvent("notification-failure", () => EngineProbeController.ReportUiFailureAsync("Windows notifications", ex));
		}
		_notifications = new AttentionNotificationList((item, preferences) =>
		{
			var reason = item.Indicator switch
			{
				TerminalTabIndicator.InputRequested => "Waiting for your answer",
				TerminalTabIndicator.Failed => "Needs attention",
				_ => "Unread update"
			};
			return new WindowsAttentionNotification(group, $"{item.OwnerName} · {item.Title}", reason, preferences,
				() => RunUiEvent("open-notification", async () =>
				{
					if (!feed.Owns(item))
					{
						return;
					}
					if (WindowState == WindowState.Minimized)
					{
						WindowState = WindowState.Normal;
					}
					Activate();
					await OpenAttentionTargetAsync(item);
				}), Report);
		}, Report);
		void Refresh(object? sender, EventArgs args)
		{
			_notifications.Update(feed.Items, App.CurrentAppearance);
		}
		void FeedChanged(object? sender, PropertyChangedEventArgs args)
		{
			if (args.PropertyName == nameof(feed.Items))
			{
				Refresh(sender, args);
			}
		}
		feed.PropertyChanged += FeedChanged;
		App.AppearanceChanged += Refresh;
		_eventDetachments.Add(() =>
		{
			feed.PropertyChanged -= FeedChanged;
			App.AppearanceChanged -= Refresh;
		});
	}

	private async Task OpenAttentionTargetAsync(AttentionItem item)
	{
		if (item.IsOrchestrator)
		{
			await EngineProbeController.SelectOrchestratorAsync();
		}
		else
		{
			await EngineProbeController.SelectItemAsync(item.Target);
		}
		if (EngineProbeController.ViewModel.Attention.Owns(item))
		{
			ProjectTree.RevealAttentionItem(item);
		}
	}

	private void WireAttentionEvents()
	{
		EngineProbeController.RefreshWindowFacts = PublishTerminalWindowFacts;
		void OnActivated(object? sender, EventArgs args)
		{
			PublishTerminalWindowFacts();
			UpdateTerminalPresentationVisibility();
			_hasTaskbarCompletionAttention = false;
			UpdateTaskbarAttention();
			// Coming back to the window is a good moment to re-read the change list.
			EngineProbeController.CurrentChanges?.RequestRefresh();
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

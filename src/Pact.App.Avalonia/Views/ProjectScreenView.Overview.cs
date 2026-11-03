using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Pact.App.Avalonia.Controllers;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class ProjectScreenView
{
	private AvaloniaMainShellController? _controller;
	private IExternalLauncher? _externalLauncher;
	private readonly List<Bitmap> _ideIcons = [];

	/// <summary>Requests the same project settings editor used by the project tree row.</summary>
	public event EventHandler<WorkspaceViewModel>? EditProjectRequested;

	internal void ConfigureOverview(AvaloniaMainShellController controller, IExternalLauncher? externalLauncher)
	{
		_controller = controller;
		_externalLauncher = externalLauncher;
	}

	internal void DetachEventProducers()
	{
		Attach(null);
		_logReloadTimer.Stop();
		_linkNoticeTimer.Stop();
		_controller = null;
		_externalLauncher = null;
	}

	private void RefreshOverviewActions()
	{
		IdeButtons.Children.Clear();
		foreach (var icon in _ideIcons)
		{
			icon.Dispose();
		}
		_ideIcons.Clear();
		ActiveReviewRows.Children.Clear();
		if (_screen is not { } screen)
		{
			return;
		}
		foreach (var ide in screen.InstalledIdes)
		{
			Control content = new TextBlock
			{
				Text = ide.Name,
				Width = 24,
				TextTrimming = global::Avalonia.Media.TextTrimming.CharacterEllipsis,
				TextAlignment = global::Avalonia.Media.TextAlignment.Center
			};
			if (ide.IconPng is { Length: > 0 } bytes)
			{
				try
				{
					using MemoryStream stream = new(bytes, writable: false);
					Bitmap icon = new(stream);
					_ideIcons.Add(icon);
					content = new Image { Source = icon, Width = 24, Height = 24 };
				}
				catch (Exception exception) when (exception is ArgumentException or IOException)
				{
					// The installation remains usable when its optional icon cannot be decoded.
				}
			}
			Button button = new() { Content = content, Margin = new Thickness(0, 0, 6, 0) };
			button.Classes.Add("overview-ide");
			ToolTip.SetTip(button, ide.Name);
			button.Click += (_, _) => RunOverviewAction(() => _controller?.OpenInstalledIdeAsync(ide) ?? Task.CompletedTask);
			IdeButtons.Children.Add(button);
		}
		foreach (var review in screen.ActiveReviews)
		{
			StackPanel row = new() { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
			foreach (var id in new[] { review.AuthorSessionId, review.ReviewerSessionId })
			{
				if (screen.Workspace.Sessions.FirstOrDefault(session => session.Record.Id == id) is not { } session)
				{
					continue;
				}
				Button participant = new() { Content = session.Title };
				participant.Click += (_, _) => RunOverviewAction(() =>
					_controller?.SelectSessionAsync(session, startIfNeeded: false) ?? Task.CompletedTask);
				row.Children.Add(participant);
			}
			row.Children.Add(new TextBlock
			{
				Text = $"{review.Turn?.ToString() ?? review.State.ToString()} · {review.Iteration}/{review.MaxIterations}",
				VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
			});
			Button log = new() { Content = "Review log" };
			log.Click += (_, _) => RunOverviewAction(async () =>
			{
				if (_controller is { } controller)
				{
					await controller.OpenProjectDocumentAsync(screen.Workspace.Id,
						Path.GetRelativePath(screen.RootPath, review.LogPath).Replace('\\', '/'));
				}
			});
			row.Children.Add(log);
			ActiveReviewRows.Children.Add(row);
		}
	}

	private void RunOverviewAction(Func<Task> action) => _eventTasks.TryRun("project-overview-action", action,
		exception =>
		{
			ShowLinkNotice(exception.Message);
			return Task.CompletedTask;
		});

	private void OnRefreshIdesClicked(object? sender, RoutedEventArgs e) =>
		RunOverviewAction(() => _controller?.RefreshInstalledIdesAsync(refresh: true) ?? Task.CompletedTask);

	private void OnEditProjectClicked(object? sender, RoutedEventArgs e)
	{
		if (_screen is { } screen)
		{
			EditProjectRequested?.Invoke(this, screen.Workspace);
		}
	}

	private void OnOpenFolderClicked(object? sender, RoutedEventArgs e)
	{
		if (_screen is { } screen)
		{
			RunOverviewAction(() => _externalLauncher?.OpenFileAsync(screen.RootPath) ?? Task.CompletedTask);
		}
	}

	private void OnRepositoryClicked(object? sender, RoutedEventArgs e)
	{
		if (_screen is { Repository: { } repository } screen
			&& ProjectScreenViewModel.GetRepositoryWebUrl(repository.RemoteText) is { } url)
		{
			RunOverviewAction(() => _controller?.OpenProjectLinkAsync(screen.Workspace,
				ProjectLinkRules.Create("Repository", url, "other", DateTimeOffset.UtcNow)) ?? Task.CompletedTask);
		}
	}

	private void OnProjectLinkClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Button { DataContext: ProjectLink link } && _screen is { } screen)
		{
			RunOverviewAction(() => _controller?.OpenProjectLinkAsync(screen.Workspace, link) ?? Task.CompletedTask);
		}
	}

	private void OnCopyProjectLinkClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Button { DataContext: ProjectLink link } && _controller is { } controller)
		{
			RunOverviewAction(async () =>
			{
				if (!await controller.Clipboard.TrySetTextAsync(link.Url))
				{
					ShowLinkNotice("Could not copy the address.");
				}
			});
		}
	}

	private void OnRemoveProjectLinkClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Button { DataContext: ProjectLink link } && _screen is { } screen && _controller is { } controller)
		{
			RunOverviewAction(() => controller.ViewModel.RemoveProjectLinkAsync(screen.Workspace.Id, link.Url, CancellationToken.None));
		}
	}
}

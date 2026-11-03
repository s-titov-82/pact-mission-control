using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AvaloniaMainShellController
{
	private Task _projectLinkCreationTail = Task.CompletedTask;
	internal IInstalledIdeService? InstalledIdeService { get; set; }

	public async Task RefreshInstalledIdesAsync(bool refresh = false, CancellationToken cancellationToken = default)
	{
		if (InstalledIdeService is not { } service)
		{
			return;
		}
		var installations = await service.DiscoverAsync(refresh, cancellationToken);
		if (!_disposed)
		{
			await InvokeUiActionAsync(() =>
			{
				foreach (var screen in _projectScreens.Values)
				{
					screen.SetInstalledIdes(installations);
				}
			});
		}
	}

	public Task OpenInstalledIdeAsync(InstalledIde ide, CancellationToken cancellationToken = default) =>
		CurrentProjectScreen is { } screen && ViewModel.Workspaces.Contains(screen.Workspace) && InstalledIdeService is { } service
			? service.OpenAsync(ide, screen.RootPath, cancellationToken) : Task.CompletedTask;

	public async Task OpenProjectLinkAsync(WorkspaceViewModel workspace, ProjectLink link, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(link);
		if (!ViewModel.Workspaces.Contains(workspace))
		{
			return;
		}
		var version = ++_projectNavigationVersion;
		var url = ProjectLinkRules.NormalizeUrl(link.Url);
		var creation = CreateOrReuseProjectLinkAsync(_projectLinkCreationTail, workspace, link.Title, url, cancellationToken);
		_projectLinkCreationTail = creation;
		var page = await creation;
		if (page is not null && version == _projectNavigationVersion && ViewModel.Workspaces.Contains(workspace))
		{
			await SelectWebPageCoreAsync(page, version, cancellationToken);
		}
	}

	private async Task<WebPageViewModel?> CreateOrReuseProjectLinkAsync(
		Task previous, WorkspaceViewModel workspace, string title, string url, CancellationToken cancellationToken)
	{
		// Recheck the saved pages after the previous creation publishes, including simultaneous
		// clicks of the same normalized URL. A failed creation must not poison later requests.
		await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
		cancellationToken.ThrowIfCancellationRequested();
		if (!ViewModel.Workspaces.Contains(workspace))
		{
			return null;
		}
		return workspace.WebPages.FirstOrDefault(candidate => SameProjectUrl(candidate.ResumeUrl, url))
			?? await ViewModel.CreateWebPageAsync(workspace.Id, title, url, cancellationToken, select: false);
	}

	private static bool SameProjectUrl(string candidate, string url) =>
		Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
		&& uri.Scheme is "https" or "http"
		&& string.Equals(uri.AbsoluteUri, url, StringComparison.Ordinal);

	private void RefreshOverview(ProjectScreenViewModel screen)
	{
		screen.SetActiveReviews(_reviewRelay.ListActive());
		_eventTasks.TryRun("overview-ides", () => RefreshInstalledIdesAsync(),
			exception => ReportUiFailureAsync("Installed IDEs", exception));
		if (screen.Repository is { } repository)
		{
			_eventTasks.TryRun("overview-git", () => repository.RefreshAsync(),
				exception => ReportUiFailureAsync("Project Git information", exception));
		}
	}
}

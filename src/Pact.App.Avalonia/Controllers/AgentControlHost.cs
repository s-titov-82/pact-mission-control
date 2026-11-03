using Pact.App.Avalonia.Lifecycle;
using Pact.Core.AgentControl;
using Pact.Core.Sessions;
using Pact.Core.Projects;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Adapts authenticated agent actions to the existing UI-owned shell and view-model operations.
/// </summary>
internal sealed class AgentControlHost : IAgentControlHost
{
	public async Task<ProjectLink> AddProjectLinkAsync(string projectId, string title, string url, string? kind, CancellationToken cancellationToken)
	{
		ProjectLink? saved = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			_ = FindLinkOwner(projectId);
			saved = await _viewModel.AddProjectLinkAsync(projectId, title, url, kind, cancellationToken);
		});
		return saved!;
	}

	public async Task<IReadOnlyList<ProjectLink>> ListProjectLinksAsync(string projectId, CancellationToken cancellationToken)
	{
		IReadOnlyList<ProjectLink> links = [];
		await _uiDispatcher.InvokeAsync(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			links = FindLinkOwner(projectId).Links.ToArray();
			return Task.CompletedTask;
		});
		return links;
	}

	private WorkspaceViewModel FindLinkOwner(string projectId) =>
		_viewModel.Workspaces.Concat(_viewModel.PausedWorkspaces)
			.FirstOrDefault(workspace => string.Equals(workspace.Id, projectId, StringComparison.Ordinal))
			?? throw new KeyNotFoundException("The project no longer exists.");

	private readonly MainWindowViewModel _viewModel;
	private readonly IUiTaskDispatcher _uiDispatcher;
	private readonly Func<string, string, CancellationToken, Task<bool>> _openProjectDocumentAsync;

	public AgentControlHost(
		MainWindowViewModel viewModel,
		IUiTaskDispatcher uiDispatcher,
		Func<string, string, CancellationToken, Task<bool>> openProjectDocumentAsync)
	{
		_openProjectDocumentAsync = openProjectDocumentAsync ?? throw new ArgumentNullException(nameof(openProjectDocumentAsync));
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
	}

	public bool TryGetOwner(string sessionId, out AgentControlOwner owner)
	{
		AgentControlOwner? resolved = null;
		_uiDispatcher.Post(() =>
		{
			if (_viewModel.RootTabs.Sessions.Any(session => string.Equals(
					session.Record.Id,
					sessionId,
					StringComparison.Ordinal)))
			{
				resolved = new AgentControlOwner(IsRoot: true, ProjectId: null);
				return;
			}

			var workspace = _viewModel.Workspaces
				.Concat(_viewModel.PausedWorkspaces)
				.FirstOrDefault(candidate => candidate.Sessions.Any(session => string.Equals(
					session.Record.Id,
					sessionId,
					StringComparison.Ordinal)));
			if (workspace is not null)
			{
				resolved = new AgentControlOwner(IsRoot: false, workspace.Id);
			}
		});

		owner = resolved ?? new AgentControlOwner(IsRoot: false, ProjectId: null);
		return resolved is not null;
	}

	public async Task<ProjectNotesSnapshot> ReadProjectNotesAsync(
		string projectId,
		CancellationToken cancellationToken)
	{
		ProjectNotesSnapshot? snapshot = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			snapshot = await _viewModel.ReadProjectNotesAsync(
				projectId,
				cancellationToken);
		});
		return snapshot!;
	}

	public async Task<ProjectNotesMutationResult> ReplaceProjectNotesAsync(
		string projectId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken)
	{
		ProjectNotesMutationResult? result = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			result = await _viewModel.ReplaceProjectNotesAsync(
				projectId,
				request.Text,
				request.ExpectedRevision,
				cancellationToken);
		});
		return result!;
	}

	public async Task<ProjectNotesMutationResult> AppendToProjectNotesAsync(
		string projectId,
		string text,
		CancellationToken cancellationToken)
	{
		ProjectNotesMutationResult? result = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			result = await _viewModel.AppendToProjectNotesAsync(
				projectId,
				text,
				cancellationToken);
		});
		return result!;
	}

	public Task CreateWebTabAsync(
		AgentControlOwner owner,
		string url,
		string? title,
		CancellationToken cancellationToken) =>
		_uiDispatcher.InvokeAsync(async () =>
		{
			if (owner.IsRoot)
			{
				await _viewModel.CreateRootWebPageAsync(
					title ?? "Web page",
					url,
					cancellationToken,
					select: false);
				return;
			}

			await _viewModel.CreateWebPageAsync(
				owner.ProjectId!,
				title ?? "Web page",
				url,
				cancellationToken,
				select: false);
		});

	public async Task<string> GetProjectRootAsync(string projectId, CancellationToken cancellationToken)
	{
		var root = string.Empty;
		await _uiDispatcher.InvokeAsync(() =>
		{
			root = _viewModel.Workspaces
				.Concat(_viewModel.PausedWorkspaces)
				.First(workspace => string.Equals(workspace.Id, projectId, StringComparison.Ordinal))
				.RootPath;
			return Task.CompletedTask;
		});
		return root;
	}

	public async Task<bool> OpenProjectDocumentAsync(
		string projectId,
		string relativePath,
		CancellationToken cancellationToken)
	{
		var shown = false;
		await _uiDispatcher.InvokeAsync(async () =>
			shown = await _openProjectDocumentAsync(projectId, relativePath, cancellationToken));
		return shown;
	}

	public async Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken)
	{
		string? directory = null;
		await _uiDispatcher.InvokeAsync(() =>
		{
			directory = _viewModel.RootTabs.Sessions
				.Concat(_viewModel.Workspaces.Concat(_viewModel.PausedWorkspaces).SelectMany(workspace => workspace.Sessions))
				.FirstOrDefault(session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal))
				?.Record.WorkingDirectory;
			return Task.CompletedTask;
		});
		return directory;
	}

	public Task AddSessionFileLinkAsync(string sessionId, AgentFileLink link, CancellationToken cancellationToken) =>
		_uiDispatcher.InvokeAsync(() => _viewModel.AddSessionFileLinkAsync(sessionId, link, cancellationToken));
}

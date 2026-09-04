using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns atomic structural changes to the persisted project tree. The caller remains
/// responsible for mirroring returned records into observable view-model collections.
/// </summary>
internal sealed class ProjectStructurePersistenceCoordinator(IProjectStore projectStore)
{
	private readonly IProjectStore _projectStore =
		projectStore ?? throw new ArgumentNullException(nameof(projectStore));

	public Task AddProjectAsync(
		ProjectRecord project,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(project);
		return _projectStore.UpdateAsync(
			document => document with
			{
				Projects = document.Projects.Concat([project]).ToArray()
			},
			cancellationToken);
	}

	public Task<ProjectRecord?> AddSessionAsync(
		string projectId,
		SessionRecord session,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				LastActiveAt = session.LastActiveAt,
				Sessions = project.Sessions.Concat([session]).ToArray()
			},
			cancellationToken);

	public Task<ProjectRecord?> AddWebPageAsync(
		string projectId,
		WebPageRecord webPage,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				LastActiveAt = webPage.LastActiveAt,
				ActiveItemId = webPage.Id,
				WebPages = project.WebPages.Concat([webPage]).ToArray()
			},
			cancellationToken);

	public Task<ProjectRecord?> MoveSessionAsync(
		string projectId,
		string sourceId,
		string targetId,
		bool insertAfter,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				Sessions = SavedItemOrder.Move(
					project.Sessions,
					session => session.Id,
					sourceId,
					targetId,
					insertAfter)
			},
			cancellationToken);

	public Task<ProjectRecord?> MoveWebPageAsync(
		string projectId,
		string sourceId,
		string targetId,
		bool insertAfter,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				WebPages = SavedItemOrder.Move(
					project.WebPages,
					webPage => webPage.Id,
					sourceId,
					targetId,
					insertAfter)
			},
			cancellationToken);

	public async Task<ProjectRecord?> EnsureNotesTabAsync(
		string projectId,
		NotesTabRecord notesTab,
		bool select,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentNullException.ThrowIfNull(notesTab);

		ProjectRecord? result = null;
		await _projectStore.UpdateAsync(
			document =>
			{
				var projects = document.Projects.Select(project =>
				{
					if (!string.Equals(project.Id, projectId, StringComparison.Ordinal))
					{
						return project;
					}

					if (project.NotesTab is not null)
					{
						result = project;
						return project;
					}

					result = project with
					{
						LastActiveAt = notesTab.LastActiveAt,
						ActiveItemId = select ? notesTab.Id : project.ActiveItemId,
						NotesTab = notesTab
					};
					return result;
				}).ToArray();
				return result is null ? document : document with { Projects = projects };
			},
			cancellationToken);
		return result;
	}

	public Task<ProjectRecord?> HideNotesTabAsync(
		string projectId,
		string noteId,
		string? replacementActiveItemId,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				NotesTab = null,
				ActiveItemId = string.Equals(project.ActiveItemId, noteId, StringComparison.Ordinal)
					? replacementActiveItemId
					: project.ActiveItemId
			},
			cancellationToken);

	public async Task<IReadOnlyList<ProjectRecord>> RemoveWebPageAsync(
		string webPageId,
		string? replacementActiveItemId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		List<ProjectRecord> updatedProjects = [];
		await _projectStore.UpdateAsync(
			document =>
			{
				var now = DateTimeOffset.UtcNow;
				var projects = document.Projects
					.Select(project =>
					{
						var webPages = project.WebPages
							.Where(webPage => !string.Equals(webPage.Id, webPageId, StringComparison.Ordinal))
							.ToArray();
						var removedFromProject = webPages.Length != project.WebPages.Count;
						var projectWithoutPage = project with { WebPages = webPages };
						var ownsReplacement = !string.IsNullOrWhiteSpace(replacementActiveItemId)
							&& ProjectOwnsItem(projectWithoutPage, replacementActiveItemId);
						if (!removedFromProject && !ownsReplacement)
						{
							return project;
						}

						var activeItemId = project.ActiveItemId;
						if (removedFromProject
							&& string.Equals(project.ActiveItemId, webPageId, StringComparison.Ordinal))
						{
							activeItemId = ownsReplacement ? replacementActiveItemId : null;
						}
						else if (ownsReplacement)
						{
							activeItemId = replacementActiveItemId;
						}

						var updatedProject = projectWithoutPage with
						{
							ActiveItemId = activeItemId,
							LastActiveAt = now
						};
						updatedProjects.Add(updatedProject);
						return updatedProject;
					})
					.ToArray();
				return updatedProjects.Count == 0
					? document
					: document with { Projects = projects };
			},
			cancellationToken);
		return updatedProjects;
	}

	public async Task<IReadOnlyList<ProjectRecord>> RemoveSessionAsync(
		string sessionId,
		string? replacementActiveItemId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		List<ProjectRecord> updatedProjects = [];
		await _projectStore.UpdateAsync(
			document =>
			{
				var projects = document.Projects
					.Select(project =>
					{
						var sessions = project.Sessions
							.Where(session => !string.Equals(session.Id, sessionId, StringComparison.Ordinal))
							.ToArray();
						var removedSession = sessions.Length != project.Sessions.Count;
						var projectWithSessions = removedSession
							? project with
							{
								LastActiveAt = DateTimeOffset.UtcNow,
								Sessions = sessions
							}
							: project;

						var activeItemId = projectWithSessions.ActiveItemId;
						if (string.Equals(project.ActiveItemId, sessionId, StringComparison.Ordinal))
						{
							activeItemId = !string.IsNullOrWhiteSpace(replacementActiveItemId)
								&& ProjectOwnsItem(projectWithSessions, replacementActiveItemId)
									? replacementActiveItemId
									: null;
						}
						else if (!string.IsNullOrWhiteSpace(replacementActiveItemId)
							&& ProjectOwnsItem(projectWithSessions, replacementActiveItemId))
						{
							activeItemId = replacementActiveItemId;
						}

						if (!string.IsNullOrWhiteSpace(activeItemId)
							&& !ProjectOwnsItem(projectWithSessions, activeItemId))
						{
							activeItemId = null;
						}

						var updatedProject = string.Equals(
							activeItemId,
							projectWithSessions.ActiveItemId,
							StringComparison.Ordinal)
								? projectWithSessions
								: projectWithSessions with { ActiveItemId = activeItemId };
						if (!ReferenceEquals(updatedProject, project))
						{
							updatedProjects.Add(updatedProject);
						}

						return updatedProject;
					})
					.ToArray();
				return updatedProjects.Count == 0
					? document
					: document with { Projects = projects };
			},
			cancellationToken);
		return updatedProjects;
	}

	public Task RemoveProjectAsync(
		string projectId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		return _projectStore.UpdateAsync(
			document => document with
			{
				Projects = document.Projects
					.Where(project => !string.Equals(project.Id, projectId, StringComparison.Ordinal))
					.ToArray()
			},
			cancellationToken);
	}

	public Task<ProjectRecord?> PauseProjectAsync(
		string projectId,
		string? activeItemId,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project =>
			{
				var ownedActiveItemId = !string.IsNullOrWhiteSpace(activeItemId)
					&& ProjectOwnsItem(project, activeItemId)
						? activeItemId
						: null;
				return WorkspacePauseService.CreatePausedWorkspace(project, ownedActiveItemId);
			},
			cancellationToken);

	public Task<ProjectRecord?> RestoreProjectAsync(
		string projectId,
		CancellationToken cancellationToken) =>
		UpdateProjectAsync(
			projectId,
			project => project with
			{
				Status = WorkspaceStatus.Active,
				LastActiveAt = DateTimeOffset.UtcNow
			},
			cancellationToken);

	private async Task<ProjectRecord?> UpdateProjectAsync(
		string projectId,
		Func<ProjectRecord, ProjectRecord> mutate,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ProjectRecord? result = null;
		await _projectStore.UpdateAsync(
			document =>
			{
				var projects = document.Projects
					.Select(project =>
					{
						if (!string.Equals(project.Id, projectId, StringComparison.Ordinal))
						{
							return project;
						}

						result = mutate(project);
						return result;
					})
					.ToArray();
				return result is null ? document : document with { Projects = projects };
			},
			cancellationToken);
		return result;
	}

	private static bool ProjectOwnsItem(ProjectRecord project, string itemId) =>
		project.Sessions.Any(session => string.Equals(session.Id, itemId, StringComparison.Ordinal))
		|| project.WebPages.Any(webPage => string.Equals(webPage.Id, itemId, StringComparison.Ordinal))
		|| string.Equals(project.NotesTab?.Id, itemId, StringComparison.Ordinal);
}

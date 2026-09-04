using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns atomic point mutations of persisted projects and their nested sessions and web pages.
/// UI projection remains the responsibility of the calling view model.
/// </summary>
internal sealed class ProjectPersistenceCoordinator(IProjectStore projectStore)
{
	private readonly IProjectStore _projectStore =
		projectStore ?? throw new ArgumentNullException(nameof(projectStore));

	public async Task<ProjectRecord?> UpdateProjectAsync(
		string projectId,
		Func<ProjectRecord, ProjectRecord> mutate,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentNullException.ThrowIfNull(mutate);

		ProjectRecord? updatedProject = null;
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

						var candidate = mutate(project);
						if (!ReferenceEquals(candidate, project))
						{
							updatedProject = candidate;
						}

						return candidate;
					})
					.ToArray();

				return updatedProject is null
					? document
					: document with { Projects = projects };
			},
			cancellationToken);
		return updatedProject;
	}

	public async Task<ProjectSessionPersistenceResult?> UpdateSessionAsync(
		string sessionId,
		Func<SessionRecord, SessionRecord> mutate,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(mutate);

		ProjectSessionPersistenceResult? result = null;
		await _projectStore.UpdateAsync(
			document =>
			{
				var projects = document.Projects
					.Select(project =>
					{
						SessionRecord? updatedSession = null;
						var sessions = project.Sessions
							.Select(session =>
							{
								if (!string.Equals(session.Id, sessionId, StringComparison.Ordinal))
								{
									return session;
								}

								var candidate = mutate(session);
								if (!ReferenceEquals(candidate, session))
								{
									updatedSession = candidate;
								}

								return candidate;
							})
							.ToArray();
						if (updatedSession is null)
						{
							return project;
						}

						var updatedProject = project with
						{
							LastActiveAt = updatedSession.LastActiveAt,
							Sessions = sessions
						};
						result = new ProjectSessionPersistenceResult(updatedProject, updatedSession);
						return updatedProject;
					})
					.ToArray();

				return result is null
					? document
					: document with { Projects = projects };
			},
			cancellationToken);
		return result;
	}

	public async Task<ProjectWebPagePersistenceResult?> UpdateWebPageAsync(
		string webPageId,
		Func<WebPageRecord, WebPageRecord> mutate,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentNullException.ThrowIfNull(mutate);

		ProjectWebPagePersistenceResult? result = null;
		await _projectStore.UpdateAsync(
			document =>
			{
				var projects = document.Projects
					.Select(project =>
					{
						WebPageRecord? updatedWebPage = null;
						var webPages = project.WebPages
							.Select(webPage =>
							{
								if (!string.Equals(webPage.Id, webPageId, StringComparison.Ordinal))
								{
									return webPage;
								}

								var candidate = mutate(webPage);
								if (!ReferenceEquals(candidate, webPage))
								{
									updatedWebPage = candidate;
								}

								return candidate;
							})
							.ToArray();
						if (updatedWebPage is null)
						{
							return project;
						}

						var updatedProject = project with
						{
							LastActiveAt = updatedWebPage.LastActiveAt,
							WebPages = webPages
						};
						result = new ProjectWebPagePersistenceResult(updatedProject, updatedWebPage);
						return updatedProject;
					})
					.ToArray();

				return result is null
					? document
					: document with { Projects = projects };
			},
			cancellationToken);
		return result;
	}
}

internal sealed record ProjectSessionPersistenceResult(
	ProjectRecord Project,
	SessionRecord Session);

internal sealed record ProjectWebPagePersistenceResult(
	ProjectRecord Project,
	WebPageRecord WebPage);

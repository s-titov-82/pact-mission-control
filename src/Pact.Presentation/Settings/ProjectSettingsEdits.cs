using Pact.Core.Projects;
using Pact.Core.Sessions;

namespace Pact.Presentation.Settings;

/// <summary>
/// A partial edit to a project's settings. Every field is optional: <see langword="null"/> means
/// "leave unchanged", so the settings form can submit only what the user touched.
/// </summary>
/// <remarks>
/// Because null means "unchanged", clearing an optional value needs its own flag — the
/// <c>Clear*</c> members below. Setting a value and its clear flag together clears it.
/// </remarks>
/// <param name="Name">New project name.</param>
/// <param name="RootPath">New project root directory.</param>
/// <param name="Notes">New notes text.</param>
/// <param name="GitLabRepoId">New GitLab project id.</param>
/// <param name="TeamCityProjectId">New TeamCity project id.</param>
/// <param name="ClearGitLabRepoId">Clears the GitLab project id.</param>
/// <param name="ClearTeamCityProjectId">Clears the TeamCity project id.</param>
public sealed record ProjectSettingsEdit(
	string? Name = null,
	string? RootPath = null,
	string? Notes = null,
	string? GitLabRepoId = null,
	string? TeamCityProjectId = null,
	bool ClearGitLabRepoId = false,
	bool ClearTeamCityProjectId = false)
{
	/// <summary>
	/// Applies this partial edit without persistence or ViewModel side effects.
	/// </summary>
	public ProjectRecord ApplyTo(ProjectRecord project, DateTimeOffset modifiedAt)
	{
		ArgumentNullException.ThrowIfNull(project);
		var result = project;
		if (Name is not null)
		{
			result = result with { Name = Name.Trim() };
		}

		if (RootPath is not null)
		{
			result = result with { RootPath = RootPath.Trim() };
		}

		if (Notes is not null)
		{
			result = result with { Notes = Notes };
		}

		if (ClearGitLabRepoId)
		{
			result = result with { GitLabRepoId = null };
		}
		else if (GitLabRepoId is not null)
		{
			result = result with { GitLabRepoId = GitLabRepoId.Trim() };
		}

		if (ClearTeamCityProjectId)
		{
			result = result with { TeamCityProjectId = null };
		}
		else if (TeamCityProjectId is not null)
		{
			result = result with { TeamCityProjectId = TeamCityProjectId.Trim() };
		}

		return ReferenceEquals(result, project)
			? project
			: result with { LastActiveAt = modifiedAt };
	}
}

/// <summary>
/// A partial edit to a session's settings, with the same "null means unchanged" convention as
/// <see cref="ProjectSettingsEdit"/>.
/// </summary>
/// <param name="Title">New tab title.</param>
/// <param name="WorkingDirectory">New working directory.</param>
/// <param name="LaunchCommand">New fresh-start command.</param>
/// <param name="ResumeCommand">New resume command.</param>
/// <param name="ClearResumeCommand">
/// Clears the resume command, so the next start begins a fresh conversation.
/// </param>
public sealed record SessionSettingsEdit(
	string? Title = null,
	string? WorkingDirectory = null,
	string? LaunchCommand = null,
	string? ResumeCommand = null,
	bool ClearResumeCommand = false)
{
	/// <summary>
	/// Applies this partial edit while retaining session identity and runtime state.
	/// </summary>
	public SessionRecord ApplyTo(SessionRecord session, DateTimeOffset modifiedAt)
	{
		ArgumentNullException.ThrowIfNull(session);
		var result = session;
		if (Title is not null)
		{
			result = result with { Title = Title.Trim() };
		}

		if (WorkingDirectory is not null)
		{
			result = result with { WorkingDirectory = WorkingDirectory.Trim() };
		}

		if (LaunchCommand is not null)
		{
			result = result with { LaunchCommand = LaunchCommand.Trim() };
		}

		if (ClearResumeCommand)
		{
			result = result with { ResumeCommand = null };
		}
		else if (ResumeCommand is not null)
		{
			result = result with { ResumeCommand = ResumeCommand };
		}

		return ReferenceEquals(result, session)
			? session
			: result with { LastActiveAt = modifiedAt };
	}
}

/// <summary>Partial settings edit for a saved ROOT browser page.</summary>
/// <param name="Title">New tab title.</param>
/// <param name="Url">New start and resume address.</param>
public sealed record RootWebPageSettingsEdit(string? Title = null, string? Url = null);

/// <summary>Applies Settings edits to project-independent ROOT items.</summary>
public interface IRootTabsSettingsEditor
{
	/// <summary>Applies a ROOT terminal edit; unknown ids are ignored.</summary>
	Task UpdateRootSessionSettingsAsync(
		string sessionId,
		SessionSettingsEdit edit,
		CancellationToken cancellationToken);

	/// <summary>Applies a ROOT browser-page edit; unknown ids are ignored.</summary>
	Task UpdateRootWebPageSettingsAsync(
		string webPageId,
		RootWebPageSettingsEdit edit,
		CancellationToken cancellationToken);
}

/// <summary>
/// Applies settings-window edits to persisted project and session state.
/// </summary>
public interface IProjectSettingsEditor
{
	/// <summary>
	/// Applies a project edit. Unknown project ids are ignored rather than throwing, since the
	/// project may have been removed while the form was open.
	/// </summary>
	Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct);

	/// <summary>
	/// Applies a session edit. Unknown session ids are ignored, for the same reason.
	/// </summary>
	Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct);

	/// <summary>
	/// Creates a project for <paramref name="directory"/>, or returns the existing one when that
	/// root is already open.
	/// </summary>
	/// <returns>The project id, or <see langword="null"/> when the directory is unusable.</returns>
	Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct);
}

using System.Collections.ObjectModel;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// One project (workspace) tab in the Projects settings section. Wraps a snapshot of a
/// <see cref="WorkspaceViewModel"/>'s <see cref="ProjectRecord"/> and, per session, its
/// <see cref="SessionRecord"/>; captures per-field baselines at construction so
/// <see cref="BuildProjectEdit"/> can emit a diff-based <see cref="ProjectSettingsEdit"/>.
/// </summary>
public sealed class ProjectItemViewModel : SettingsObservableObject
{
	private string _name;
	private string _baselineName;
	private string _rootPath;
	private string _baselineRootPath;
	private string _notes;
	private string? _baselineNotes;
	private string _gitLabRepoId;
	private string? _baselineGitLabRepoId;
	private string _teamCityProjectId;
	private string? _baselineTeamCityProjectId;
	private SessionSettingsItemViewModel? _selectedSession;

	/// <summary>Creates a tab over the loaded project, capturing its values as the baseline.</summary>
	public ProjectItemViewModel(WorkspaceViewModel workspace)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		var record = workspace.Record;
		Id = record.Id;
		StatusDisplay = record.Status.ToString();
		CreatedAtDisplay = FormatTimestamp(record.CreatedAt);
		LastActiveAtDisplay = FormatTimestamp(record.LastActiveAt);

		_name = record.Name;
		_baselineName = record.Name;
		_rootPath = record.RootPath;
		_baselineRootPath = record.RootPath;
		_notes = record.Notes ?? string.Empty;
		_baselineNotes = record.Notes;
		_gitLabRepoId = record.GitLabRepoId ?? string.Empty;
		_baselineGitLabRepoId = record.GitLabRepoId;
		_teamCityProjectId = record.TeamCityProjectId ?? string.Empty;
		_baselineTeamCityProjectId = record.TeamCityProjectId;

		Sessions = [];
		foreach (var session in workspace.Sessions)
		{
			AttachSession(new SessionSettingsItemViewModel(session));
		}

		_selectedSession = Sessions.Count > 0 ? Sessions[0] : null;
	}

	/// <summary>Raised whenever an editable field on this project or one of its sessions changes.</summary>
	public event EventHandler? Changed;

	/// <summary>Project id.</summary>
	public string Id { get; }
	/// <summary>Project status as display text.</summary>
	public string StatusDisplay { get; }
	/// <summary>Creation time as display text.</summary>
	public string CreatedAtDisplay { get; }
	/// <summary>Last activity time as display text.</summary>
	public string LastActiveAtDisplay { get; }

	/// <summary>Compact one-line read-only summary shown above the editable fields.</summary>
	public string InfoLine => $"{Id} · {StatusDisplay} · created {CreatedAtDisplay} · active {LastActiveAtDisplay}";

	/// <summary>False for a project without terminal sessions; collapses the Sessions block.</summary>
	public bool HasSessions => Sessions.Count > 0;

	/// <summary>Project display name.</summary>
	public string Name
	{
		get => _name;
		set
		{
			if (SetField(ref _name, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Project root directory.</summary>
	public string RootPath
	{
		get => _rootPath;
		set
		{
			if (SetField(ref _rootPath, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Project notes text.</summary>
	public string Notes
	{
		get => _notes;
		set
		{
			if (SetField(ref _notes, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>GitLab project id substituted into web link templates.</summary>
	public string GitLabRepoId
	{
		get => _gitLabRepoId;
		set
		{
			if (SetField(ref _gitLabRepoId, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>TeamCity project id substituted into web link templates.</summary>
	public string TeamCityProjectId
	{
		get => _teamCityProjectId;
		set
		{
			if (SetField(ref _teamCityProjectId, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Session tabs nested under this project.</summary>
	public ObservableCollection<SessionSettingsItemViewModel> Sessions { get; }

	/// <summary>Selected session tab, or <see langword="null"/> when none is selected.</summary>
	public SessionSettingsItemViewModel? SelectedSession
	{
		get => _selectedSession;
		set => SetField(ref _selectedSession, value);
	}

	/// <summary>True when this project's own fields differ from their baseline, or any session is dirty.</summary>
	public bool IsItemDirty =>
		!ProjectFieldDiff.TrimEquals(Name, _baselineName) ||
		!ProjectFieldDiff.TrimEquals(RootPath, _baselineRootPath) ||
		!ProjectFieldDiff.TrimEquals(Notes, _baselineNotes) ||
		!ProjectFieldDiff.TrimEquals(GitLabRepoId, _baselineGitLabRepoId) ||
		!ProjectFieldDiff.TrimEquals(TeamCityProjectId, _baselineTeamCityProjectId) ||
		Sessions.Any(session => session.IsItemDirty);

	/// <summary>Tab caption, marked with a bullet while dirty.</summary>
	public string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Name)
				? Name
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new project)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	/// <summary>
	/// Emits only the fields whose trimmed current value differs from its baseline. Nullable
	/// project fields (GitLab/TeamCity ids) set the matching Clear flag when a previously
	/// non-empty baseline becomes empty. Repository/branch hints are no longer edited here, so
	/// their <see cref="ProjectSettingsEdit"/> members are always left at their no-op defaults.
	/// </summary>
	public ProjectSettingsEdit BuildProjectEdit()
	{
		var name = ProjectFieldDiff.TrimEquals(Name, _baselineName) ? null : Name;
		var rootPath = ProjectFieldDiff.TrimEquals(RootPath, _baselineRootPath) ? null : RootPath;
		var notes = ProjectFieldDiff.TrimEquals(Notes, _baselineNotes) ? null : Notes;
		(var gitLabRepoId, var clearGitLabRepoId) = ProjectFieldDiff.DiffNullable(_baselineGitLabRepoId, GitLabRepoId);
		(var teamCityProjectId, var clearTeamCityProjectId) = ProjectFieldDiff.DiffNullable(_baselineTeamCityProjectId, TeamCityProjectId);

		return new ProjectSettingsEdit(
			name,
			rootPath,
			notes,
			gitLabRepoId,
			teamCityProjectId,
			ClearGitLabRepoId: clearGitLabRepoId,
			ClearTeamCityProjectId: clearTeamCityProjectId);
	}

	/// <summary>Name must be non-empty; RootPath must be an existing directory.</summary>
	public string? Validate()
	{
		if (string.IsNullOrWhiteSpace(Name))
		{
			return "Every project needs a non-empty name.";
		}

		if (!Directory.Exists(RootPath))
		{
			return $"Project '{Name}' root path does not exist: {RootPath}";
		}

		return null;
	}

	/// <summary>Resets baselines to the current field values after a successful save.</summary>
	internal void Rebaseline()
	{
		_baselineName = Name;
		_baselineRootPath = RootPath;
		_baselineNotes = Notes;
		_baselineGitLabRepoId = GitLabRepoId;
		_baselineTeamCityProjectId = TeamCityProjectId;
		RaiseChanged();
	}

	private void AttachSession(SessionSettingsItemViewModel session)
	{
		session.Changed += OnSessionChanged;
		Sessions.Add(session);
	}

	private void OnSessionChanged(object? sender, EventArgs e) => RaiseChanged();

	private void RaiseChanged()
	{
		OnPropertyChanged(nameof(IsItemDirty));
		OnPropertyChanged(nameof(TabHeader));
		Changed?.Invoke(this, EventArgs.Empty);
	}

	private static string FormatTimestamp(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// One session tab nested under a <see cref="ProjectItemViewModel"/>. Locked sessions (currently
/// driven by a running scenario) always produce an empty edit regardless of any field changes.
/// </summary>
public sealed class SessionSettingsItemViewModel : SettingsObservableObject
{
	private string _title;
	private string _baselineTitle;
	private string _workingDirectory;
	private string _baselineWorkingDirectory;
	private string _launchCommand;
	private string _baselineLaunchCommand;
	private string _resumeCommand;
	private string? _baselineResumeCommand;

	/// <summary>Creates a tab over the loaded session, capturing its values as the baseline.</summary>
	public SessionSettingsItemViewModel(
		SessionViewModel session,
		bool showWorkingDirectoryForAllKinds = false)
	{
		ArgumentNullException.ThrowIfNull(session);

		var record = session.Record;
		Id = record.Id;
		ShowWorkingDirectorySetting =
			showWorkingDirectoryForAllKinds || record.Kind is AgentKind.Pwsh or AgentKind.Custom;
		KindDisplay = record.Kind.ToString();
		StatusDisplay = record.Status.ToString();
		IsLocked = session.IsLockedByScenario;

		_title = record.Title;
		_baselineTitle = record.Title;
		_workingDirectory = record.WorkingDirectory;
		_baselineWorkingDirectory = record.WorkingDirectory;
		_launchCommand = record.LaunchCommand;
		_baselineLaunchCommand = record.LaunchCommand;
		_resumeCommand = record.ResumeCommand ?? string.Empty;
		_baselineResumeCommand = record.ResumeCommand;
	}

	/// <summary>Raised whenever one of this session's editable fields changes.</summary>
	public event EventHandler? Changed;

	/// <summary>Session id.</summary>
	public string Id { get; }

	/// <summary>Agent kind as display text.</summary>
	public string KindDisplay { get; }

	/// <summary>Session status as display text.</summary>
	public string StatusDisplay { get; }

	/// <summary>Compact one-line read-only summary shown above the editable fields.</summary>
	public string InfoLine => $"{Id} · {KindDisplay} · {StatusDisplay}";

	/// <summary>
	/// True only for Pwsh/Custom sessions, which honor an explicit working directory. Agent kinds
	/// (Codex, Claude, Hermes) always run in project context, so the row is hidden and
	/// <see cref="Validate"/> skips the directory check.
	/// </summary>
	public bool ShowWorkingDirectorySetting { get; }

	/// <summary>Captured at load from <see cref="SessionViewModel.IsLockedByScenario"/>.</summary>
	public bool IsLocked { get; }

	/// <summary>Tab title.</summary>
	public string Title
	{
		get => _title;
		set
		{
			if (SetField(ref _title, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Directory the process is launched in.</summary>
	public string WorkingDirectory
	{
		get => _workingDirectory;
		set
		{
			if (SetField(ref _workingDirectory, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Command line used for a fresh start.</summary>
	public string LaunchCommand
	{
		get => _launchCommand;
		set
		{
			if (SetField(ref _launchCommand, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Command line that resumes the previous conversation; blank clears it.</summary>
	public string ResumeCommand
	{
		get => _resumeCommand;
		set
		{
			if (SetField(ref _resumeCommand, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Whether this session holds unsaved edits.</summary>
	public bool IsItemDirty =>
		!ProjectFieldDiff.TrimEquals(Title, _baselineTitle) ||
		!ProjectFieldDiff.TrimEquals(WorkingDirectory, _baselineWorkingDirectory) ||
		!ProjectFieldDiff.TrimEquals(LaunchCommand, _baselineLaunchCommand) ||
		!ProjectFieldDiff.TrimEquals(ResumeCommand, _baselineResumeCommand);

	/// <summary>
	/// Emits only the fields whose trimmed current value differs from its baseline. Always empty
	/// for a locked session, regardless of any edited fields.
	/// </summary>
	public SessionSettingsEdit BuildSessionEdit()
	{
		if (IsLocked)
		{
			return new SessionSettingsEdit();
		}

		var title = ProjectFieldDiff.TrimEquals(Title, _baselineTitle) ? null : Title;
		var workingDirectory = ProjectFieldDiff.TrimEquals(WorkingDirectory, _baselineWorkingDirectory) ? null : WorkingDirectory;
		var launchCommand = ProjectFieldDiff.TrimEquals(LaunchCommand, _baselineLaunchCommand) ? null : LaunchCommand;
		(var resumeCommand, var clearResumeCommand) = ProjectFieldDiff.DiffNullable(_baselineResumeCommand, ResumeCommand);

		return new SessionSettingsEdit(title, workingDirectory, launchCommand, resumeCommand, clearResumeCommand);
	}

	/// <summary>
	/// Title/LaunchCommand must be non-empty; WorkingDirectory must be an existing directory, but
	/// only when the setting is shown (<see cref="ShowWorkingDirectorySetting"/>).
	/// </summary>
	public string? Validate()
	{
		if (string.IsNullOrWhiteSpace(Title))
		{
			return "Every session needs a non-empty title.";
		}

		if (string.IsNullOrWhiteSpace(LaunchCommand))
		{
			return $"Session '{Title}' needs a launch command.";
		}

		if (ShowWorkingDirectorySetting && !Directory.Exists(WorkingDirectory))
		{
			return $"Session '{Title}' working directory does not exist: {WorkingDirectory}";
		}

		return null;
	}

	/// <summary>Resets baselines to the current field values after a successful save.</summary>
	internal void Rebaseline()
	{
		_baselineTitle = Title;
		_baselineWorkingDirectory = WorkingDirectory;
		_baselineLaunchCommand = LaunchCommand;
		_baselineResumeCommand = ResumeCommand;
		RaiseChanged();
	}

	private void RaiseChanged()
	{
		OnPropertyChanged(nameof(IsItemDirty));
		Changed?.Invoke(this, EventArgs.Empty);
	}
}

/// <summary>Shared baseline-diffing helpers for <see cref="ProjectItemViewModel"/> and <see cref="SessionSettingsItemViewModel"/>.</summary>
internal static class ProjectFieldDiff
{
	/// <summary>Trimmed equality; a null baseline is treated as empty.</summary>
	public static bool TrimEquals(string current, string? baseline)
		=> string.Equals(current.Trim(), (baseline ?? string.Empty).Trim(), StringComparison.Ordinal);

	/// <summary>
	/// Diffs a nullable field: an empty baseline plus a non-empty current value returns the new
	/// value; a non-empty baseline plus an empty current value returns a Clear flag; otherwise
	/// returns no change (null value, no clear) when trimmed values match.
	/// </summary>
	public static (string? Value, bool Clear) DiffNullable(string? baseline, string current)
	{
		var trimmedCurrent = current.Trim();
		var baselineEmpty = string.IsNullOrWhiteSpace(baseline);

		if (baselineEmpty)
		{
			return string.IsNullOrEmpty(trimmedCurrent) ? (null, false) : (current, false);
		}

		if (string.IsNullOrEmpty(trimmedCurrent))
		{
			return (null, true);
		}

		return string.Equals(baseline!.Trim(), trimmedCurrent, StringComparison.Ordinal)
			? (null, false)
			: (current, false);
	}
}
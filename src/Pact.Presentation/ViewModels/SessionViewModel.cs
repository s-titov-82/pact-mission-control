using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// A terminal session row in the project tree, carrying its derived tab indicator and scenario
/// lock state.
/// </summary>
public sealed class SessionViewModel : INotifyPropertyChanged
{
	private readonly string? _projectRootPath;

	/// <summary>
	/// Creates a view model over a saved session.
	/// </summary>
	/// <param name="record">Persisted session state.</param>
	/// <param name="projectRootPath">
	/// Owning project's root. Supplied so a session running in the project root can hide its
	/// redundant working-directory subtitle.
	/// </param>
	/// <param name="isRootItem">Whether the session belongs to the project-independent ROOT area.</param>
	public SessionViewModel(
		SessionRecord record,
		string? projectRootPath = null,
		bool isRootItem = false)
	{
		Record = record;
		_projectRootPath = projectRootPath;
		IsRootItem = isRootItem;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Persisted session state.</summary>
	public SessionRecord Record { get; private set; }

	/// <summary>Whether the session belongs to the project-independent ROOT area.</summary>
	public bool IsRootItem { get; }

	/// <summary>Whether the user explicitly paused this ROOT session.</summary>
	public bool IsManuallyPaused { get; private set; }

	/// <summary>Whether the ROOT row offers its pause action.</summary>
	public bool CanPause => IsRootItem && !IsManuallyPaused;

	/// <summary>Whether the ROOT row offers its resume action.</summary>
	public bool CanResume => IsRootItem && IsManuallyPaused;

	/// <summary>Tab label.</summary>
	public string Title => Record.Title;

	/// <summary>Label used when this session is offered as a send target.</summary>
	public string TargetDisplayName => Title;

	/// <summary>Agent kind as a lower-case token, used to select the row's icon.</summary>
	public string TerminalKind => Record.Kind.ToString().ToLowerInvariant();

	/// <summary>Working directory text, empty when it matches the project root.</summary>
	public string WorkingDirectoryText => ShowWorkingDirectory
		? Record.WorkingDirectory
		: string.Empty;

	/// <summary>
	/// Whether the working directory differs from the project root and is therefore worth showing.
	/// </summary>
	public bool ShowWorkingDirectory => !PathsEqual(Record.WorkingDirectory, _projectRootPath);

	/// <summary>Secondary row text.</summary>
	public string Subtitle => WorkingDirectoryText;

	/// <summary>Session status as text, for binding.</summary>
	public string Status => Record.Status.ToString();

	/// <summary>
	/// Badge derived by the status engine. UI evidence only — never read as scenario progress.
	/// </summary>
	public TerminalTabIndicator Indicator { get; private set; }

	/// <summary>
	/// Whether the tree row needs its single status cell for an engine indicator or an explicit
	/// ROOT pause glyph.
	/// </summary>
	public bool ShowStatusIndicator =>
		Indicator != TerminalTabIndicator.None || IsManuallyPaused;

	/// <summary>
	/// Whether the single status cell displays the pause glyph from terminal lifecycle evidence
	/// or explicit per-item ROOT pause state.
	/// </summary>
	public bool ShowPausedIndicator =>
		Indicator == TerminalTabIndicator.Paused || IsManuallyPaused;

	/// <summary>Latest accepted classifier text displayed below the terminal title.</summary>
	public string StatusDescription { get; private set; } = string.Empty;

	/// <summary>Whether this tab is the selected item.</summary>
	public bool IsCurrentTerminal { get; private set; }

	/// <summary>Run holding this session's input lock, or <see langword="null"/> when unlocked.</summary>
	public string? LockedByScenarioRunId { get; private set; }

	/// <summary>
	/// Whether a scenario blocks manual input. The session stays visible and scrollable.
	/// </summary>
	public bool IsLockedByScenario => LockedByScenarioRunId is not null;

	/// <summary>Whether this agent supports a conversation-reset command.</summary>
	public bool CanResetAgentSession =>
		!IsManuallyPaused && AgentResetCommands.TryGetResetCommand(Record.Kind, out _);

	/// <summary>When the current activity began, used to show elapsed busy time.</summary>
	public DateTimeOffset BusySince { get; private set; }

	internal void ApplyTerminalStatus(
		TerminalTabIndicator indicator,
		DateTimeOffset? activityStartedAt,
		string statusDescription)
	{
		if (StatusDescription != statusDescription)
		{
			StatusDescription = statusDescription;
			OnPropertyChanged(nameof(StatusDescription));
		}

		if (activityStartedAt is DateTimeOffset startedAt && BusySince != startedAt)
		{
			BusySince = startedAt;
			OnPropertyChanged(nameof(BusySince));
		}

		if (Indicator == indicator)
		{
			return;
		}

		Indicator = indicator;
		OnPropertyChanged(nameof(Indicator));
		OnPropertyChanged(nameof(ShowStatusIndicator));
		OnPropertyChanged(nameof(ShowPausedIndicator));
	}

	/// <summary>Sets whether this tab is the selected item.</summary>
	public void SetCurrentTerminal(bool isCurrentTerminal)
	{
		if (IsCurrentTerminal == isCurrentTerminal)
		{
			return;
		}

		IsCurrentTerminal = isCurrentTerminal;
		OnPropertyChanged(nameof(IsCurrentTerminal));
	}

	/// <summary>Projects the persisted per-item ROOT pause state onto the row.</summary>
	public void SetManuallyPaused(bool isManuallyPaused)
	{
		if (IsManuallyPaused == isManuallyPaused)
		{
			return;
		}

		IsManuallyPaused = isManuallyPaused;
		OnPropertyChanged(nameof(IsManuallyPaused));
		OnPropertyChanged(nameof(ShowStatusIndicator));
		OnPropertyChanged(nameof(ShowPausedIndicator));
		OnPropertyChanged(nameof(CanPause));
		OnPropertyChanged(nameof(CanResume));
		OnPropertyChanged(nameof(CanResetAgentSession));
	}

	/// <summary>Blocks manual input while <paramref name="runId"/> is using this session.</summary>
	public void LockForScenario(string runId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runId);
		if (string.Equals(LockedByScenarioRunId, runId, StringComparison.Ordinal))
		{
			return;
		}

		LockedByScenarioRunId = runId;
		OnPropertyChanged(nameof(LockedByScenarioRunId));
		OnPropertyChanged(nameof(IsLockedByScenario));
	}

	/// <summary>
	/// Restores manual input. Also used on a watchdog pause to free just the stuck session so
	/// the user can answer it.
	/// </summary>
	public void UnlockFromScenario()
	{
		if (LockedByScenarioRunId is null)
		{
			return;
		}

		LockedByScenarioRunId = null;
		OnPropertyChanged(nameof(LockedByScenarioRunId));
		OnPropertyChanged(nameof(IsLockedByScenario));
	}

	/// <summary>
	/// Replaces the persisted state and raises change notifications for the derived properties.
	/// </summary>
	public void UpdateRecord(SessionRecord record)
	{
		if (Equals(Record, record))
		{
			return;
		}

		Record = record;
		OnPropertyChanged(nameof(Record));
		OnPropertyChanged(nameof(Title));
		OnPropertyChanged(nameof(TargetDisplayName));
		OnPropertyChanged(nameof(TerminalKind));
		OnPropertyChanged(nameof(WorkingDirectoryText));
		OnPropertyChanged(nameof(ShowWorkingDirectory));
		OnPropertyChanged(nameof(Subtitle));
		OnPropertyChanged(nameof(Status));
		OnPropertyChanged(nameof(CanResetAgentSession));
	}

	private static bool PathsEqual(string path, string? otherPath)
	{
		if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(otherPath))
		{
			return false;
		}

		return string.Equals(
			NormalizePath(path),
			NormalizePath(otherPath),
			StringComparison.OrdinalIgnoreCase);
	}

	private static string NormalizePath(string path) => path.Trim()
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

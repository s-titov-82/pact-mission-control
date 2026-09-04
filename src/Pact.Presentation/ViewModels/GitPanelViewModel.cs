using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Pact.Core.Git;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// State behind the git panel for one project: the parsed working-tree snapshot, the configured
/// button set, and the commands the panel can run.
/// </summary>
/// <remarks>
/// Git invocations are serialized behind <see cref="IsBusy"/>, so buttons disable while one runs
/// rather than allowing overlapping writes to the same repository.
/// </remarks>
public sealed class GitPanelViewModel : INotifyPropertyChanged
{
	private const int MaxLogLength = 64 * 1024;
	private readonly ActivityTrackingRunner _runner;
	private readonly Action<ResolvedGitHelperAction, string, string> _launchHelperAction;
	private readonly Func<string, bool> _directoryExists;
	private readonly SynchronizationContext? _synchronizationContext;
	private readonly StringBuilder _log = new();
	private int _runningGitCommands;

	/// <summary>Creates the panel model for one repository.</summary>
	public GitPanelViewModel(
		string rootPath,
		IGitCliRunner runner,
		IReadOnlyList<ResolvedGitHelperAction> helperActions,
		Action<ResolvedGitHelperAction, string, string> launchHelperAction,
		Func<string, bool> directoryExists,
		GitButtonCommandSet? commands = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		ArgumentNullException.ThrowIfNull(runner);
		ArgumentNullException.ThrowIfNull(helperActions);
		ArgumentNullException.ThrowIfNull(launchHelperAction);
		ArgumentNullException.ThrowIfNull(directoryExists);

		RootPath = rootPath;
		_runner = new ActivityTrackingRunner(runner, this);
		_launchHelperAction = launchHelperAction;
		_directoryExists = directoryExists;
		_synchronizationContext = SynchronizationContext.Current;
		Commands = commands ?? GitButtonCommandSet.Create(null);
		HelperActions = helperActions
			.Where(action => action.Slot is "history" or "custom")
			.ToArray();
		ResolveHelperAction = helperActions.FirstOrDefault(action => action.Slot == "resolve");
		PopupButtons = Commands.PopupButtons
			.Select(descriptor => new GitPopupButtonViewModel(this, descriptor))
			.ToArray();
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Configured remote name, or empty when the repository has none.</summary>
	public string RemoteText { get; private set; } = string.Empty;
	/// <summary>Repository root this panel operates on.</summary>
	public string RootPath { get; }
	/// <summary>Current branch, or the commit id when HEAD is detached.</summary>
	public string BranchText { get; private set; } = string.Empty;
	/// <summary>Ahead/behind counts relative to the upstream, formatted for display.</summary>
	public string AheadBehindText { get; private set; } = string.Empty;
	/// <summary>Working-tree change counts, formatted for display.</summary>
	public string SummaryText { get; private set; } = string.Empty;
	/// <summary>Whether any path is conflicted, which blocks actions needing a settled tree.</summary>
	public bool HasConflicts { get; private set; }
	/// <summary>Number of stash entries.</summary>
	public int StashCount { get; private set; }
	/// <summary>Whether anything is stashed and can be popped.</summary>
	public bool HasStash => StashCount > 0;
	/// <summary>Whether the tree holds changes worth stashing.</summary>
	public bool HasStashableChanges => Snapshot?.Files.Any(IsStashableChange) == true;
	/// <summary>Whether a rebase is mid-flight, which offers abort instead of the usual actions.</summary>
	public bool IsRebaseInProgress { get; private set; }
	/// <summary>Whether a git command is running; buttons stay disabled meanwhile.</summary>
	public bool IsBusy { get; private set; }
	/// <summary>Convenience inverse of <see cref="IsBusy"/> for binding button state.</summary>
	public bool IsNotBusy => !IsBusy;
	/// <summary>
	/// Whether the panel is waiting for any git invocation, including the status refreshes that
	/// run outside <see cref="IsBusy"/>. This is the panel's activity signal: while it is set the
	/// log is still expected to grow, so a finished-looking log is not yet a finished command.
	/// </summary>
	public bool IsGitRunning => Volatile.Read(ref _runningGitCommands) > 0;
	/// <summary>Accumulated git output shown in the panel log.</summary>
	public string LogText => _log.ToString();
	/// <summary>External helper actions available on this machine.</summary>
	public IReadOnlyList<ResolvedGitHelperAction> HelperActions { get; }
	/// <summary>Helper bound to the conflict-resolution slot, or <see langword="null"/> when none is installed.</summary>
	public ResolvedGitHelperAction? ResolveHelperAction { get; }
	/// <summary>Latest parsed status, or <see langword="null"/> before the first refresh.</summary>
	public GitStatusSnapshot? Snapshot { get; private set; }

	/// <summary>Configured popup button commands (git-helpers.json), fixed for this VM's lifetime.</summary>
	public GitButtonCommandSet Commands { get; }

	/// <summary>User-added command entries rendered as extra popup buttons.</summary>
	public IReadOnlyList<GitCustomCommand> CustomCommands => Commands.CustomCommands;

	/// <summary>
	/// All popup buttons (built-in and custom, interleaved) in the configured "commands" array
	/// order, for the popup's single data-driven button row.
	/// </summary>
	public IReadOnlyList<GitPopupButtonViewModel> PopupButtons { get; }

	/// <summary>Whether the pull button is enabled in settings.</summary>
	public bool IsPullVisible => Commands.IsEnabled(GitButtonCommandSet.PullId);
	/// <summary>Whether the push button is enabled in settings.</summary>
	public bool IsPushVisible => Commands.IsEnabled(GitButtonCommandSet.PushId);
	/// <summary>Whether the commit button is enabled in settings.</summary>
	public bool IsCommitVisible => Commands.IsEnabled(GitButtonCommandSet.CommitId);
	/// <summary>Whether the branch-switch button is enabled in settings.</summary>
	public bool IsSwitchVisible => Commands.IsEnabled(GitButtonCommandSet.SwitchId);
	/// <summary>Whether the rebase button is enabled in settings.</summary>
	public bool IsRebaseVisible => Commands.IsEnabled(GitButtonCommandSet.RebaseId);
	/// <summary>Whether the merge button is enabled in settings.</summary>
	public bool IsMergeVisible => Commands.IsEnabled(GitButtonCommandSet.MergeId);
	/// <summary>Whether stashing is offered: enabled in settings and something is stashable.</summary>
	public bool ShowStashButton => Commands.IsEnabled(GitButtonCommandSet.StashId) && HasStashableChanges;
	/// <summary>Whether popping is offered: enabled in settings and something is stashed.</summary>
	public bool ShowPopStashButton => Commands.IsEnabled(GitButtonCommandSet.StashPopId) && HasStash;

	/// <summary>Re-reads git status and updates every derived property.</summary>
	public async Task RefreshAsync(CancellationToken cancellationToken = default)
	{
		var status = await _runner.RunAsync(
			RootPath,
			["--no-optional-locks", "status", "--porcelain=v2", "--branch"],
			outputLine: null,
			cancellationToken);
		if (!status.Succeeded)
		{
			ClearSnapshot();
			AppendLogLine(status.StandardError.Trim());
			return;
		}

		Snapshot = GitStatusParser.Parse(status.StandardOutput);
		OnPropertyChanged(nameof(Snapshot));
		OnPropertyChanged(nameof(HasStashableChanges));
		OnPropertyChanged(nameof(ShowStashButton));
		NotifyPopupButtonStates();
		SetBranchText(Snapshot.Branch);
		SetAheadBehindText(FormatAheadBehind(Snapshot.Ahead, Snapshot.Behind));
		SetSummaryText(FormatSummary(Snapshot));
		SetHasConflicts(Snapshot.HasConflicts);

		SetRemoteText(await ReadRemoteTextAsync(cancellationToken));

		var stash = await _runner.RunAsync(
			RootPath,
			["rev-list", "--walk-reflogs", "--count", "refs/stash"],
			outputLine: null,
			cancellationToken);
		SetStashCount(stash.Succeeded && int.TryParse(stash.StandardOutput.Trim(), out var count) ? count : 0);

		SetIsRebaseInProgress(await ReadRebaseInProgressAsync(cancellationToken));
	}

	/// <summary>
	/// Runs one git command, streaming its output into the log and refreshing status afterwards.
	/// </summary>
	public async Task RunCommandAsync(
		string title,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);

		if (IsBusy)
		{
			return;
		}

		SetIsBusy(true);
		try
		{
			await ExecuteGitCommandAsync(
				title,
				arguments,
				refreshAfter: true,
				clearLogBeforeRun: true,
				cancellationToken);
		}
		finally
		{
			SetIsBusy(false);
		}
	}

	/// <summary>
	/// Runs the planned rebase-onto-base sequence, stopping at the first failing step so the
	/// repository is not left further mid-operation.
	/// </summary>
	public async Task RunRebaseOntoBaseScenarioAsync(CancellationToken cancellationToken = default)
	{
		if (IsBusy)
		{
			return;
		}

		SetIsBusy(true);
		try
		{
			ClearLog();
			await RefreshAsync(cancellationToken);
			if (Snapshot is null)
			{
				return;
			}

			if (Snapshot.HasConflicts)
			{
				AppendLogLine("Cannot rebase while the working tree has conflicts.");
				return;
			}

			if (Snapshot.IsDetached)
			{
				AppendLogLine("Cannot rebase from a detached HEAD.");
				return;
			}

			var baseBranch = await DetectBaseBranchAsync(cancellationToken);
			if (baseBranch is null)
			{
				AppendLogLine("No master/main branch found.");
				return;
			}

			var steps = GitRebaseOntoBasePlanner.Plan(
				Snapshot.IsDirty,
				baseBranch,
				Snapshot.Branch,
				Commands);
			for (var index = 0; index < steps.Count; index++)
			{
				var step = steps[index];
				AppendLogLine($"# {step.Title}");
				var result = await ExecuteGitCommandAsync(
					step.Title,
					step.Arguments,
					refreshAfter: false,
					clearLogBeforeRun: false,
					cancellationToken);
				if (!result.Succeeded)
				{
					if (step.Arguments.Count > 0 && step.Arguments[0] == "rebase")
					{
						AppendLogLine("Resolve conflicts via Resolve, then run git rebase --continue.");
					}

					foreach (var skipped in steps.Skip(index + 1))
					{
						AppendLogLine($"Skipped: {skipped.Title}");
					}

					break;
				}
			}

			await RefreshAsync(cancellationToken);
		}
		finally
		{
			SetIsBusy(false);
		}
	}

	/// <summary>Aborts an in-progress rebase, returning the tree to its pre-rebase state.</summary>
	public async Task AbortRebaseAsync(CancellationToken cancellationToken = default) => await RunCommandAsync("Abort rebase", ["rebase", "--abort"], cancellationToken);

	/// <summary>Launches the configured merge tool to resolve conflicts.</summary>
	public async Task ResolveAsync(CancellationToken cancellationToken = default)
	{
		if (ResolveHelperAction is not null)
		{
			_launchHelperAction(ResolveHelperAction, RootPath, Snapshot?.Branch ?? string.Empty);
			return;
		}

		var result = await RunCommandAndReturnResultAsync("Resolve conflicts",
			["mergetool", "-y"],
			cancellationToken);
		if (!result.Succeeded
			&& result.StandardError.Contains("merge", StringComparison.OrdinalIgnoreCase)
			&& (result.StandardError.Contains("tool", StringComparison.OrdinalIgnoreCase)
				|| result.StandardError.Contains("program", StringComparison.OrdinalIgnoreCase)))
		{
			AppendLogLine("Configure a merge tool with: git config --global merge.tool <tool>");
		}
	}

	/// <summary>Starts an external helper for this repository and branch.</summary>
	public void LaunchHelperAction(ResolvedGitHelperAction action) => _launchHelperAction(action, RootPath, Snapshot?.Branch ?? string.Empty);

	/// <summary>Appends a failure message to the panel log.</summary>
	public void ReportError(string message)
	{
		ClearLog();
		AppendLogLine(message);
	}

	private async Task<GitCommandResult> RunCommandAndReturnResultAsync(
		string title,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		if (IsBusy)
		{
			return new GitCommandResult(-1, string.Empty, "git operation already running");
		}

		SetIsBusy(true);
		try
		{
			return await ExecuteGitCommandAsync(
				title,
				arguments,
				refreshAfter: true,
				clearLogBeforeRun: true,
				cancellationToken);
		}
		finally
		{
			SetIsBusy(false);
		}
	}

	private async Task<GitCommandResult> ExecuteGitCommandAsync(
		string title,
		IReadOnlyList<string> arguments,
		bool refreshAfter,
		bool clearLogBeforeRun,
		CancellationToken cancellationToken)
	{
		if (clearLogBeforeRun)
		{
			ClearLog();
		}

		AppendLogLine($"> git {string.Join(' ', arguments)}");
		var result = await _runner.RunAsync(
			RootPath,
			arguments,
			new ContextProgress(_synchronizationContext, AppendLogLine),
			cancellationToken);
		if (!result.Succeeded && !string.IsNullOrWhiteSpace(result.StandardError))
		{
			AppendLogLine(result.StandardError.Trim());
		}

		if (refreshAfter)
		{
			await RefreshAsync(cancellationToken);
		}

		// The outcome is logged after the refresh so the last line means the panel is done, not
		// only the process.
		AppendLogLine(result.Succeeded
			? $"{title} ok"
			: $"{title} failed (exit {result.ExitCode})");
		return result;
	}

	private void ClearLog()
	{
		if (_log.Length == 0)
		{
			return;
		}

		_log.Clear();
		OnPropertyChanged(nameof(LogText));
	}

	private async Task<bool> ReadRebaseInProgressAsync(CancellationToken cancellationToken)
	{
		foreach (var name in new[] { "rebase-merge", "rebase-apply" })
		{
			var result = await _runner.RunAsync(
				RootPath,
				["rev-parse", "--git-path", name],
				outputLine: null,
				cancellationToken);
			var gitPath = result.StandardOutput.Trim();
			if (result.Succeeded && !Path.IsPathFullyQualified(gitPath))
			{
				gitPath = Path.GetFullPath(gitPath, RootPath);
			}

			if (result.Succeeded && _directoryExists(gitPath))
			{
				return true;
			}
		}

		return false;
	}

	private async Task<string> ReadRemoteTextAsync(CancellationToken cancellationToken)
	{
		var origin = await _runner.RunAsync(
			RootPath,
			["remote", "get-url", "origin"],
			outputLine: null,
			cancellationToken);
		var originText = origin.StandardOutput.Trim();
		if (origin.Succeeded && !string.IsNullOrWhiteSpace(originText))
		{
			return originText;
		}

		var remotes = await _runner.RunAsync(
			RootPath,
			["remote", "-v"],
			outputLine: null,
			cancellationToken);
		if (!remotes.Succeeded)
		{
			return "no remote";
		}

		foreach (var line in remotes.StandardOutput.Split(
					 ['\r', '\n'],
					 StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length >= 2 && line.Contains("(fetch)", StringComparison.Ordinal))
			{
				return parts[1];
			}
		}

		return "no remote";
	}

	private async Task<string?> DetectBaseBranchAsync(CancellationToken cancellationToken)
	{
		foreach (var branch in new[] { "master", "main" })
		{
			var result = await _runner.RunAsync(
				RootPath,
				["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"],
				outputLine: null,
				cancellationToken);
			if (result.Succeeded)
			{
				return branch;
			}
		}

		return null;
	}

	private void AppendLogLine(string? line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return;
		}

		_log.AppendLine(line);
		if (_log.Length > MaxLogLength)
		{
			_log.Remove(0, _log.Length - MaxLogLength);
		}

		OnPropertyChanged(nameof(LogText));
	}

	private static string FormatAheadBehind(int ahead, int behind)
	{
		List<string> parts = [];
		if (ahead > 0)
		{
			parts.Add($"↑{ahead}");
		}

		if (behind > 0)
		{
			parts.Add($"↓{behind}");
		}

		return string.Join(' ', parts);
	}

	private static string FormatSummary(GitStatusSnapshot snapshot)
	{
		List<string> parts = [$"+{snapshot.Added}", $"~{snapshot.Modified}", $"-{snapshot.Deleted}"];

		if (snapshot.Untracked > 0)
		{
			parts.Add($"?{snapshot.Untracked}");
		}

		if (snapshot.Conflicted > 0)
		{
			parts.Add($"!{snapshot.Conflicted}");
		}

		return string.Join(' ', parts);
	}

	private static bool IsStashableChange(GitFileEntry file) => file.Kind is GitChangeKind.Added or GitChangeKind.Modified or GitChangeKind.Deleted;

	private void ClearSnapshot()
	{
		Snapshot = null;
		OnPropertyChanged(nameof(Snapshot));
		OnPropertyChanged(nameof(HasStashableChanges));
		OnPropertyChanged(nameof(ShowStashButton));
		NotifyPopupButtonStates();
		SetBranchText(string.Empty);
		SetAheadBehindText(string.Empty);
		SetSummaryText(string.Empty);
		SetHasConflicts(false);
		SetStashCount(0);
		SetIsRebaseInProgress(false);
	}

	private void SetRemoteText(string value)
	{
		if (RemoteText == value)
		{
			return;
		}

		RemoteText = value;
		OnPropertyChanged(nameof(RemoteText));
	}

	private void SetBranchText(string value)
	{
		if (BranchText == value)
		{
			return;
		}

		BranchText = value;
		OnPropertyChanged(nameof(BranchText));
	}

	private void SetAheadBehindText(string value)
	{
		if (AheadBehindText == value)
		{
			return;
		}

		AheadBehindText = value;
		OnPropertyChanged(nameof(AheadBehindText));
	}

	private void SetSummaryText(string value)
	{
		if (SummaryText == value)
		{
			return;
		}

		SummaryText = value;
		OnPropertyChanged(nameof(SummaryText));
	}

	private void SetHasConflicts(bool value)
	{
		if (HasConflicts == value)
		{
			return;
		}

		HasConflicts = value;
		OnPropertyChanged(nameof(HasConflicts));
	}

	private void SetStashCount(int value)
	{
		if (StashCount == value)
		{
			return;
		}

		StashCount = value;
		OnPropertyChanged(nameof(StashCount));
		OnPropertyChanged(nameof(HasStash));
		OnPropertyChanged(nameof(ShowPopStashButton));
		NotifyPopupButtonStates();
	}

	private void SetIsRebaseInProgress(bool value)
	{
		if (IsRebaseInProgress == value)
		{
			return;
		}

		IsRebaseInProgress = value;
		OnPropertyChanged(nameof(IsRebaseInProgress));
	}

	private void SetIsBusy(bool value)
	{
		if (IsBusy == value)
		{
			return;
		}

		IsBusy = value;
		OnPropertyChanged(nameof(IsBusy));
		OnPropertyChanged(nameof(IsNotBusy));
		NotifyPopupButtonStates();
	}

	private void EnterGitInvocation()
	{
		if (Interlocked.Increment(ref _runningGitCommands) == 1)
		{
			OnPropertyChanged(nameof(IsGitRunning));
		}
	}

	private void ExitGitInvocation()
	{
		if (Interlocked.Decrement(ref _runningGitCommands) == 0)
		{
			OnPropertyChanged(nameof(IsGitRunning));
		}
	}

	private void NotifyPopupButtonStates()
	{
		foreach (var button in PopupButtons)
		{
			button.NotifyStateChanged();
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	/// <summary>
	/// Counts every git invocation the panel makes, wherever it is started from, so activity is
	/// reported from one place instead of each call site.
	/// </summary>
	private sealed class ActivityTrackingRunner(IGitCliRunner inner, GitPanelViewModel owner) : IGitCliRunner
	{
		public async Task<GitCommandResult> RunAsync(
			string workingDirectory,
			IReadOnlyList<string> arguments,
			IProgress<string>? outputLine,
			CancellationToken cancellationToken)
		{
			owner.EnterGitInvocation();
			try
			{
				return await inner.RunAsync(workingDirectory, arguments, outputLine, cancellationToken);
			}
			finally
			{
				owner.ExitGitInvocation();
			}
		}
	}

	private sealed class ContextProgress : IProgress<string>
	{
		private readonly SynchronizationContext? _context;
		private readonly Action<string> _report;

		public ContextProgress(SynchronizationContext? context, Action<string> report)
		{
			_context = context;
			_report = report;
		}

		public void Report(string value)
		{
			if (_context is null || ReferenceEquals(SynchronizationContext.Current, _context))
			{
				_report(value);
				return;
			}

			_context.Post(static state =>
			{
				(var report, var value) = ((Action<string>, string))state!;
				report(value);
			}, (_report, value));
		}
	}
}

/// <summary>
/// One button in the popup's data-driven main row: wraps a <see cref="GitPopupButtonDescriptor"/>
/// with dynamic <see cref="IsEnabled"/> state mirrored from the owning
/// <see cref="GitPanelViewModel"/> (busy state, stashable changes, existing stash).
/// </summary>
public sealed class GitPopupButtonViewModel : INotifyPropertyChanged
{
	private readonly GitPanelViewModel _owner;

	internal GitPopupButtonViewModel(GitPanelViewModel owner, GitPopupButtonDescriptor descriptor)
	{
		_owner = owner;
		Id = descriptor.Id;
		Label = descriptor.Label;
		Kind = descriptor.Kind;
		CustomArguments = descriptor.CustomArguments;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Button id, matching a built-in id or a user-added command's id.</summary>
	public string Id { get; }

	/// <summary>Button caption.</summary>
	public string Label { get; }

	/// <summary>How the button dispatches its click.</summary>
	public GitPopupButtonKind Kind { get; }

	/// <summary>
	/// Pre-split arguments for a custom command, so the click handler need not resolve the id
	/// again. <see langword="null"/> for built-in buttons.
	/// </summary>
	public IReadOnlyList<string>? CustomArguments { get; }

	/// <summary>Every configured button keeps its layout slot. Buttons disabled in settings are
	/// already filtered out of <see cref="GitButtonCommandSet.PopupButtons"/>.</summary>
	[SuppressMessage(
		"Performance",
		"CA1822:Mark members as static",
		Justification = "Avalonia binds this property through each button instance and it participates in INotifyPropertyChanged.")]
	public bool IsVisible => true;

	/// <summary>State-dependent actions remain visible but disabled when they cannot run.</summary>
	public bool IsEnabled => _owner.IsNotBusy && Id switch
	{
		GitButtonCommandSet.StashId => _owner.HasStashableChanges,
		GitButtonCommandSet.StashPopId => _owner.HasStash,
		_ => true
	};

	internal void NotifyStateChanged()
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
	}
}

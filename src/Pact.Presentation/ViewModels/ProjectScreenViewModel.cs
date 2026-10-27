using System.ComponentModel;
using Pact.Core.Projects;

namespace Pact.Presentation.ViewModels;

/// <summary>A tab of the project screen.</summary>
public enum ProjectScreenTab
{
	/// <summary>Project name, root and branch; the future dashboard.</summary>
	Overview,

	/// <summary>Uncommitted changes, for git repositories only.</summary>
	Changes,

	/// <summary>The review logs in <c>.pact/reviews</c>.</summary>
	ReviewLogs
}

/// <summary>How a request to delete a review log ended.</summary>
public enum ReviewLogDeletion
{
	/// <summary>The log is gone.</summary>
	Deleted,

	/// <summary>A running review still writes the log; it stays.</summary>
	InUse,

	/// <summary>The log is not in the folder.</summary>
	NotFound
}

/// <summary>How a request to delete every review log ended.</summary>
/// <param name="Deleted">Logs removed.</param>
/// <param name="Kept">Logs left in place because running reviews still write them.</param>
public sealed record ReviewLogBulkDeletion(int Deleted, int Kept);

/// <summary>The project screen shown in the center pane: overview, changes and review logs.</summary>
public sealed class ProjectScreenViewModel : INotifyPropertyChanged
{
	private readonly IProjectMarkdownFileStore _fileStore;
	private readonly Func<string, bool> _isLogInUse;
	private int _logVersion;

	/// <summary>Creates the screen of one project; <paramref name="changes"/> is null outside git.</summary>
	/// <param name="workspace">The project.</param>
	/// <param name="changes">The project's change list.</param>
	/// <param name="fileStore">Reads the review logs.</param>
	/// <param name="isLogInUse">Whether a running review writes the log at a project-relative path.</param>
	public ProjectScreenViewModel(
		WorkspaceViewModel workspace,
		ProjectChangesViewModel? changes,
		IProjectMarkdownFileStore fileStore,
		Func<string, bool>? isLogInUse = null)
	{
		Workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		Changes = changes;
		_fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
		_isLogInUse = isLogInUse ?? (static _ => false);
		changes?.PropertyChanged += OnChangesPropertyChanged;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>The project this screen shows.</summary>
	public WorkspaceViewModel Workspace { get; }

	/// <summary>The project's change list, or <see langword="null"/> outside git.</summary>
	public ProjectChangesViewModel? Changes { get; }

	/// <summary>Whether the Changes tab exists.</summary>
	public bool HasChanges => Changes is not null;

	/// <summary>The tab shown.</summary>
	public ProjectScreenTab SelectedTab { get; private set; } = ProjectScreenTab.Overview;

	/// <summary>Project name.</summary>
	public string Name => Workspace.Name;

	/// <summary>Project root folder.</summary>
	public string RootPath => Workspace.RootPath;

	/// <summary>Current branch, empty outside git or before the first read.</summary>
	public string Branch => Changes?.Branch ?? string.Empty;

	/// <summary>Review logs, newest first, as project-relative paths.</summary>
	public IReadOnlyList<string> ReviewLogs { get; private set; } = [];

	/// <summary>Whether the folder holds any review log.</summary>
	public bool HasReviewLogs => ReviewLogs.Count > 0;

	/// <summary>The log shown, or <see langword="null"/>.</summary>
	public string? SelectedReviewLog { get; private set; }

	/// <summary>Text of the log shown.</summary>
	public string ReviewLogText { get; private set; } = string.Empty;

	/// <summary>
	/// Shows a tab. Changes is ignored outside git; Review logs re-lists the logs and re-reads the
	/// open one, or opens the newest when none is selected.
	/// </summary>
	public async Task SelectTabAsync(ProjectScreenTab tab, CancellationToken cancellationToken)
	{
		if (tab == ProjectScreenTab.Changes && Changes is null)
		{
			return;
		}

		SetTab(tab);
		if (tab == ProjectScreenTab.ReviewLogs)
		{
			RefreshReviewLogs();
			var log = SelectedReviewLog is not null && ReviewLogs.Contains(SelectedReviewLog)
				? SelectedReviewLog
				: ReviewLogs.Count > 0 ? ReviewLogs[0] : null;
			await SelectReviewLogAsync(log, cancellationToken).ConfigureAwait(true);
		}
	}

	/// <summary>
	/// Shows one log's text, or clears it for <see langword="null"/>. Of several picks in flight,
	/// only the last one publishes its text.
	/// </summary>
	public async Task SelectReviewLogAsync(string? relativePath, CancellationToken cancellationToken)
	{
		var version = ++_logVersion;
		if (!string.Equals(SelectedReviewLog, relativePath, StringComparison.Ordinal))
		{
			SelectedReviewLog = relativePath;
			Raise(nameof(SelectedReviewLog));
		}

		var text = relativePath is null ? string.Empty : await LoadLogAsync(relativePath, cancellationToken);
		if (version == _logVersion)
		{
			SetLogText(text);
		}
	}

	/// <summary>Re-reads the open log while Review logs is shown, so turns appended to it appear.</summary>
	public async Task ReloadReviewLogAsync(CancellationToken cancellationToken)
	{
		if (SelectedTab != ProjectScreenTab.ReviewLogs || SelectedReviewLog is not { } log)
		{
			return;
		}

		var version = _logVersion;
		var text = await LoadLogAsync(log, cancellationToken);
		if (version == _logVersion)
		{
			SetLogText(text);
		}
	}

	private async Task<string> LoadLogAsync(string relativePath, CancellationToken cancellationToken) =>
		(await _fileStore.LoadAsync(
			Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)),
			cancellationToken).ConfigureAwait(true)).Text;

	private void SetLogText(string text)
	{
		if (!string.Equals(ReviewLogText, text, StringComparison.Ordinal))
		{
			ReviewLogText = text;
			Raise(nameof(ReviewLogText));
		}
	}

	/// <summary>Switches to Review logs and shows the given log.</summary>
	/// <returns><see langword="false"/> when the log is not in the folder; nothing changes then.</returns>
	public async Task<bool> OpenReviewLogAsync(string relativePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		RefreshReviewLogs();
		var match = ReviewLogs.FirstOrDefault(log => string.Equals(log, relativePath, StringComparison.OrdinalIgnoreCase));
		if (match is null)
		{
			return false;
		}

		SetTab(ProjectScreenTab.ReviewLogs);
		await SelectReviewLogAsync(match, cancellationToken).ConfigureAwait(true);
		return true;
	}

	/// <summary>
	/// Deletes a review log unless a running review still writes it. A deleted shown log gives way
	/// to the one after it, or the one before it when it was the oldest.
	/// </summary>
	public async Task<ReviewLogDeletion> DeleteReviewLogAsync(string relativePath, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		RefreshReviewLogs();
		var index = ReviewLogs.ToList().IndexOf(relativePath);
		if (index < 0)
		{
			return ReviewLogDeletion.NotFound;
		}

		if (_isLogInUse(relativePath))
		{
			return ReviewLogDeletion.InUse;
		}

		ReviewLogFiles.Delete(RootPath, relativePath);
		RefreshReviewLogs();
		if (string.Equals(SelectedReviewLog, relativePath, StringComparison.Ordinal))
		{
			var next = ReviewLogs.Count == 0 ? null : ReviewLogs[Math.Min(index, ReviewLogs.Count - 1)];
			await SelectReviewLogAsync(next, cancellationToken).ConfigureAwait(true);
		}

		return ReviewLogDeletion.Deleted;
	}

	/// <summary>
	/// Deletes every review log a running review does not write. A deleted shown log gives way to
	/// the newest log left.
	/// </summary>
	public async Task<ReviewLogBulkDeletion> DeleteAllReviewLogsAsync(CancellationToken cancellationToken)
	{
		RefreshReviewLogs();
		var deleted = 0;
		var kept = 0;
		foreach (var log in ReviewLogs)
		{
			if (_isLogInUse(log))
			{
				kept++;
				continue;
			}

			ReviewLogFiles.Delete(RootPath, log);
			deleted++;
		}

		RefreshReviewLogs();
		if (SelectedReviewLog is not null && !ReviewLogs.Contains(SelectedReviewLog))
		{
			await SelectReviewLogAsync(ReviewLogs.Count == 0 ? null : ReviewLogs[0], cancellationToken)
				.ConfigureAwait(true);
		}

		return new ReviewLogBulkDeletion(deleted, kept);
	}

	private void RefreshReviewLogs()
	{
		var logs = ReviewLogFiles.List(RootPath);
		if (!logs.SequenceEqual(ReviewLogs))
		{
			ReviewLogs = logs;
			Raise(nameof(ReviewLogs));
			Raise(nameof(HasReviewLogs));
		}
	}

	private void SetTab(ProjectScreenTab tab)
	{
		if (SelectedTab != tab)
		{
			SelectedTab = tab;
			Raise(nameof(SelectedTab));
		}
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectChangesViewModel.Branch))
		{
			Raise(nameof(Branch));
		}
	}

	private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

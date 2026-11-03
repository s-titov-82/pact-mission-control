using System.ComponentModel;
using Pact.Core.Projects;
using Pact.Core.Platform;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.ViewModels;

/// <summary>A tab of the project screen.</summary>
public enum ProjectScreenTab
{
	/// <summary>Project name, root and branch; the future dashboard.</summary>
	Overview,

	/// <summary>Project Markdown documents with their retained editor and preview.</summary>
	Docs,

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
	private readonly Func<string, string?, CancellationToken, Task<bool>> _openDocument;
	private readonly Func<string, bool> _isLogInUse;
	private int _logVersion;
	private int _tabVersion;

	/// <summary>Creates the screen of one project; <paramref name="changes"/> is null outside git.</summary>
	/// <param name="workspace">The project.</param>
	/// <param name="changes">The project's change list.</param>
	/// <param name="fileStore">Reads the review logs.</param>
	/// <param name="isLogInUse">Whether a running review writes the log at a project-relative path.</param>
	/// <param name="remarks">The remark threads of the Changes tab; null outside git.</param>
	/// <param name="openDocument">Shows a project Markdown file in the Docs pane, for links in logs and rendered files.</param>
	/// <param name="documents">The project's retained document owner, shared with Notes operations.</param>
	/// <param name="repository">The existing Git panel also supplies the repository address.</param>
	public ProjectScreenViewModel(
		WorkspaceViewModel workspace,
		ProjectChangesViewModel? changes,
		IProjectMarkdownFileStore fileStore,
		Func<string, bool>? isLogInUse = null,
		ChangesRemarksViewModel? remarks = null,
		Func<string, string?, CancellationToken, Task<bool>>? openDocument = null,
		DocsAndNotesWorkspaceViewModel? documents = null,
		GitPanelViewModel? repository = null)
	{
		Repository = repository;
		Documents = documents;
		Remarks = remarks;
		_openDocument = openDocument ?? (static (_, _, _) => Task.FromResult(false));
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

	/// <summary>The retained Notes and Markdown documents shared with project MCP operations.</summary>
	public DocsAndNotesWorkspaceViewModel? Documents { get; }

	/// <summary>Invalidates pending tab and log publication when this retained screen leaves its owner surface.</summary>
	public void Deactivate()
	{
		_tabVersion++;
		_logVersion++;
		Documents?.Deactivate();
	}

	/// <summary>The existing project Git information, or null outside Git.</summary>
	public GitPanelViewModel? Repository { get; }

	/// <summary>Returns a browser address for HTTP(S), SSH or SCP Git remotes; local/unknown remotes have no web target.</summary>
	public static string? GetRepositoryWebUrl(string remote)
	{
		ArgumentNullException.ThrowIfNull(remote);
		if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri))
		{
			var at = remote.IndexOf('@', StringComparison.Ordinal);
			var colon = remote.IndexOf(':', at + 1);
			if (at < 0 || colon <= at + 1
				|| !Uri.TryCreate("https://" + remote[(at + 1)..colon] + "/" + remote[(colon + 1)..], UriKind.Absolute, out uri))
			{
				return null;
			}
		}
		if (uri.Scheme is not ("http" or "https" or "ssh"))
		{
			return null;
		}
		UriBuilder web = new(uri) { UserName = string.Empty, Password = string.Empty };
		if (uri.Scheme == "ssh")
		{
			web.Scheme = "https";
			web.Port = -1;
		}
		if (web.Path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
		{
			web.Path = web.Path[..^4];
		}
		return web.Uri.AbsoluteUri;
	}

	/// <summary>The shared discovery result, never project-specific launch state.</summary>
	public IReadOnlyList<InstalledIde> InstalledIdes { get; private set; } = [];

	/// <summary>The live relay reviews of this project.</summary>
	public IReadOnlyList<ReviewSummary> ActiveReviews { get; private set; } = [];

	/// <summary>Publishes a shared completed discovery without changing project selection.</summary>
	public void SetInstalledIdes(IReadOnlyList<InstalledIde> installations)
	{
		InstalledIdes = installations;
		Raise(nameof(InstalledIdes));
	}

	/// <summary>Projects the existing relay state, excluding completed and other-project reviews.</summary>
	public void SetActiveReviews(IReadOnlyList<ReviewSummary> reviews)
	{
		ActiveReviews = reviews.Where(review => review.ProjectId == Workspace.Id).ToArray();
		Raise(nameof(ActiveReviews));
	}

	/// <summary>The project's change list, or <see langword="null"/> outside git.</summary>
	public ProjectChangesViewModel? Changes { get; }

	/// <summary>The remark threads of the Changes tab, or <see langword="null"/> outside git.</summary>
	public ChangesRemarksViewModel? Remarks { get; }

	/// <summary>Whether the Changes tab exists.</summary>
	public bool HasChanges => Changes is not null;

	/// <summary>The tab shown.</summary>
	public ProjectScreenTab SelectedTab { get; private set; } = ProjectScreenTab.Overview;

	/// <summary>Project name.</summary>
	public string Name => Workspace.Name;

	/// <summary>Project root folder.</summary>
	public string RootPath => Workspace.RootPath;

	/// <summary>
	/// Shows a project Markdown file in the project's Docs pane, at a heading when one is given, as
	/// a link in a log or a rendered file asks.
	/// </summary>
	/// <returns><see langword="false"/> when the pane cannot show it.</returns>
	public Task<bool> OpenDocumentAsync(string relativePath, string? heading, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		return _openDocument(relativePath, heading, cancellationToken);
	}

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
	/// Shows a tab. Changes opens the first file when none is selected and is ignored outside git;
	/// Review logs re-lists the logs and re-reads the
	/// open one, or opens the newest when none is selected.
	/// </summary>
	/// <returns>Whether the tab and its document selection remained current through loading.</returns>
	public async Task<bool> SelectTabAsync(ProjectScreenTab tab, CancellationToken cancellationToken)
	{
		if (tab == ProjectScreenTab.Changes && Changes is null)
		{
			return false;
		}

		var version = ++_tabVersion;
		_logVersion++;
		TabNavigationRequested?.Invoke(this, EventArgs.Empty);
		if (Documents is { } documents)
		{
			if (tab is ProjectScreenTab.Overview or ProjectScreenTab.Docs)
			{
				if (!await documents.SelectSectionAsync(tab == ProjectScreenTab.Overview
					? DocsAndNotesSection.Notes : DocsAndNotesSection.Docs, cancellationToken))
				{
					return false;
				}
			}
			else
			{
				documents.Deactivate();
				await documents.FlushAsync(cancellationToken);
			}
		}
		if (version != _tabVersion)
		{
			return false;
		}
		SetTab(tab);
		if (Changes is { } changes)
		{
			await changes.SetAutoSelectFirstFileAsync(tab == ProjectScreenTab.Changes, cancellationToken).ConfigureAwait(true);
		}
		if (tab == ProjectScreenTab.ReviewLogs)
		{
			RefreshReviewLogs();
			var log = SelectedReviewLog is not null && ReviewLogs.Contains(SelectedReviewLog)
				? SelectedReviewLog
				: ReviewLogs.Count > 0 ? ReviewLogs[0] : null;
			await SelectReviewLogAsync(log, cancellationToken).ConfigureAwait(true);
		}
		return version == _tabVersion;
	}

	/// <summary>Announces an accepted tab request synchronously, before its document or log I/O.</summary>
	public event EventHandler? TabNavigationRequested;

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

	/// <summary>Refreshes the log catalog and checks a target without changing the selected tab.</summary>
	public bool ContainsReviewLog(string relativePath)
	{
		RefreshReviewLogs();
		return ReviewLogs.Contains(relativePath, StringComparer.OrdinalIgnoreCase);
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

		var version = ++_tabVersion;
		if (Documents is { } documents)
		{
			documents.Deactivate();
			await documents.FlushAsync(cancellationToken);
		}
		if (version != _tabVersion)
		{
			return false;
		}
		SetTab(ProjectScreenTab.ReviewLogs);
		if (Changes is { } changes)
		{
			await changes.SetAutoSelectFirstFileAsync(false, cancellationToken).ConfigureAwait(true);
		}
		await SelectReviewLogAsync(match, cancellationToken).ConfigureAwait(true);
		return version == _tabVersion;
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
	/// the newest log left. A failed delete stops the run; the list still shows what is left.
	/// </summary>
	public async Task<ReviewLogBulkDeletion> DeleteAllReviewLogsAsync(CancellationToken cancellationToken)
	{
		RefreshReviewLogs();
		var deleted = 0;
		var kept = 0;
		try
		{
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
		}
		finally
		{
			await ReconcileReviewLogsAsync(cancellationToken).ConfigureAwait(true);
		}

		return new ReviewLogBulkDeletion(deleted, kept);
	}

	private async Task ReconcileReviewLogsAsync(CancellationToken cancellationToken)
	{
		RefreshReviewLogs();
		if (SelectedReviewLog is not null && !ReviewLogs.Contains(SelectedReviewLog))
		{
			await SelectReviewLogAsync(ReviewLogs.Count == 0 ? null : ReviewLogs[0], cancellationToken)
				.ConfigureAwait(true);
		}
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

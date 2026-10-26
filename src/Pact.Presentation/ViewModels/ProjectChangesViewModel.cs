using System.ComponentModel;
using System.Globalization;
using Pact.Core.Git;

namespace Pact.Presentation.ViewModels;

/// <summary>What one row of the diff view shows.</summary>
public enum DiffRowKind
{
	/// <summary>The <c>@@</c> line that opens a hunk.</summary>
	HunkHeader,

	/// <summary>Unchanged context line.</summary>
	Context,

	/// <summary>Added line.</summary>
	Added,

	/// <summary>Removed line.</summary>
	Removed
}

/// <summary>One row of the diff view.</summary>
/// <param name="Kind">Row kind.</param>
/// <param name="OldNumber">Old line number as text, empty when the line has none.</param>
/// <param name="NewNumber">New line number as text, empty when the line has none.</param>
/// <param name="Text">Line text, or the hunk header.</param>
public sealed record DiffRowViewModel(DiffRowKind Kind, string OldNumber, string NewNumber, string Text)
{
	/// <summary>Whether the row opens a hunk.</summary>
	public bool IsHunkHeader => Kind == DiffRowKind.HunkHeader;

	/// <summary>Whether the row is an added line.</summary>
	public bool IsAdded => Kind == DiffRowKind.Added;

	/// <summary>Whether the row is a removed line.</summary>
	public bool IsRemoved => Kind == DiffRowKind.Removed;
}

/// <summary>One changed file in the Changes list.</summary>
public sealed class ChangedFileViewModel
{
	/// <summary>Wraps a git status entry.</summary>
	public ChangedFileViewModel(GitFileEntry entry)
	{
		Entry = entry ?? throw new ArgumentNullException(nameof(entry));
		var separator = entry.Path.LastIndexOf('/');
		FileName = separator < 0 ? entry.Path : entry.Path[(separator + 1)..];
		Folder = separator < 0 ? string.Empty : entry.Path[..separator];
		StatusLetter = entry.OriginalPath is not null
			? "R"
			: entry.Kind switch
			{
				GitChangeKind.Added => "A",
				GitChangeKind.Deleted => "D",
				GitChangeKind.Untracked => "U",
				GitChangeKind.Conflicted => "C",
				_ => "M"
			};
	}

	/// <summary>The status entry this row shows.</summary>
	public GitFileEntry Entry { get; }

	/// <summary>Repository-relative path with forward slashes.</summary>
	public string Path => Entry.Path;

	/// <summary>File name without its folder.</summary>
	public string FileName { get; }

	/// <summary>Folder part of the path, empty at the repository root.</summary>
	public string Folder { get; }

	/// <summary>One-letter change kind: M, A, D, R, U or C.</summary>
	public string StatusLetter { get; }

	/// <summary>Whether the index differs from HEAD for this file.</summary>
	public bool IsStaged => Entry.IsStaged;
}

/// <summary>
/// A project's uncommitted changes and the diff of the selected file. While observed it is
/// reconciled with git on a timer and on request, never more often than
/// <see cref="MinRefreshInterval"/>; all UI-facing state changes go through the UI poster.
/// </summary>
public sealed class ProjectChangesViewModel : INotifyPropertyChanged, IDisposable
{
	/// <summary>Shortest time between two reads of git status.</summary>
	public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(2);

	/// <summary>Period of the reconciliation that catches changes no file event reports.</summary>
	public static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(5);

	/// <summary>Diff lines shown before the rest is hidden behind "Show all".</summary>
	public const int MaxShownLines = 3000;

	private readonly IGitWorkingTreeReader _reader;
	private readonly TimeProvider _time;
	private readonly Action<Action> _postToUi;
	private readonly Lock _gate = new();
	private readonly ITimer _timer;
	private readonly CancellationTokenSource _disposal = new();
	private int _observers;
	private DateTimeOffset _lastStart = DateTimeOffset.MinValue;
	private bool _inFlight;
	private bool _requested;
	private bool _disposed;
	private volatile ChangedFileViewModel[] _files = [];
	private volatile string? _selectedPath;
	private int _selectionVersion;
	private FileDiff? _selectedDiff;
	private bool _showAll;

	/// <summary>Creates the change list of one project root.</summary>
	public ProjectChangesViewModel(
		string projectRoot,
		IGitWorkingTreeReader reader,
		TimeProvider time,
		Action<Action> postToUi)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ProjectRoot = projectRoot;
		_reader = reader ?? throw new ArgumentNullException(nameof(reader));
		_time = time ?? throw new ArgumentNullException(nameof(time));
		_postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
		_timer = _time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>The repository root this list reads.</summary>
	public string ProjectRoot { get; }

	/// <summary>Changed files, in git status order.</summary>
	public IReadOnlyList<ChangedFileViewModel> Files { get; private set; } = [];

	/// <summary>The file whose diff is shown, or <see langword="null"/>.</summary>
	public ChangedFileViewModel? SelectedFile { get; private set; }

	/// <summary>Placeholder text of the selected file's diff, or <see langword="null"/> for a line diff.</summary>
	public string? DiffPlaceholder => _selectedDiff?.Placeholder;

	/// <summary>Rows of the selected file's diff, cut at <see cref="MaxShownLines"/> lines.</summary>
	public IReadOnlyList<DiffRowViewModel> DiffRows { get; private set; } = [];

	/// <summary>Diff lines hidden by the cut.</summary>
	public int HiddenLineCount { get; private set; }

	/// <summary>Text of the link that shows the hidden lines.</summary>
	public string ShowAllText => string.Create(
		CultureInfo.InvariantCulture,
		$"Show all ({HiddenLineCount} more lines)");

	/// <summary>Number of changed files.</summary>
	public int FileCount => Files.Count;

	/// <summary>Lines added in tracked files.</summary>
	public int AddedLines { get; private set; }

	/// <summary>Lines removed in tracked files.</summary>
	public int RemovedLines { get; private set; }

	/// <summary>Current branch, empty until the first read.</summary>
	public string Branch { get; private set; } = string.Empty;

	/// <summary>The HEAD commit the list compares with, or <see langword="null"/> while HEAD is unborn.</summary>
	public string? HeadCommit { get; private set; }

	/// <summary>False when the last read failed, for instance outside a repository.</summary>
	public bool IsAvailable { get; private set; } = true;

	/// <summary>Title of the right-column entry.</summary>
	public string EntryTitle => FileCount == 0
		? "No uncommitted changes"
		: string.Create(CultureInfo.InvariantCulture, $"Changes · {FileCount}");

	/// <summary>Line totals under the entry title; empty for a clean tree.</summary>
	public string EntrySubtitle => FileCount == 0
		? string.Empty
		: string.Create(CultureInfo.InvariantCulture, $"+{AddedLines} −{RemovedLines}");

	/// <summary>Header of the project screen's Changes tab.</summary>
	public string TabHeader => FileCount == 0
		? "Changes"
		: string.Create(CultureInfo.InvariantCulture, $"Changes {FileCount}");

	/// <summary>
	/// Keeps the list reconciled until the returned handle is disposed; the first observation
	/// reads at once.
	/// </summary>
	public IDisposable Observe()
	{
		bool first;
		lock (_gate)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_observers++;
			first = _observers == 1;
		}

		if (first)
		{
			// A project shown again right after a read still waits out the minimum interval.
			RequestRefresh();
		}

		return new Observation(this);
	}

	/// <summary>
	/// Brings the next read forward: at once when <see cref="MinRefreshInterval"/> has passed
	/// since the last one started, otherwise as soon as it has. Ignored while unobserved.
	/// </summary>
	public void RequestRefresh()
	{
		lock (_gate)
		{
			if (_disposed || _observers == 0)
			{
				return;
			}

			_requested = true;
			if (_inFlight)
			{
				return;
			}

			var wait = _lastStart + MinRefreshInterval - _time.GetUtcNow();
			if (wait > TimeSpan.Zero)
			{
				_timer.Change(wait, Timeout.InfiniteTimeSpan);
				return;
			}
		}

		StartRefresh();
	}

	/// <summary>Selects a file and reads its diff.</summary>
	public async Task SelectFileAsync(ChangedFileViewModel? file, CancellationToken cancellationToken)
	{
		var version = Interlocked.Increment(ref _selectionVersion);
		_selectedPath = file?.Path;
		_showAll = false;
		SelectedFile = file;
		Raise(nameof(SelectedFile));
		if (file is null)
		{
			ApplyDiff(null);
			return;
		}

		var diff = await _reader.ReadDiffAsync(ProjectRoot, file.Entry, HeadCommit, cancellationToken).ConfigureAwait(false);
		_postToUi(() =>
		{
			// A later pick, even of the same file, has its own read in flight.
			if (Volatile.Read(ref _selectionVersion) == version)
			{
				ApplyDiff(diff);
			}
		});
	}

	/// <summary>Shows the lines the cut hid.</summary>
	public void ShowAllLines()
	{
		_showAll = true;
		ApplyDiff(_selectedDiff);
	}

	/// <inheritdoc />
	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
		}

		_timer.Dispose();
		_disposal.Cancel();
		_disposal.Dispose();
	}

	private void OnTimer()
	{
		lock (_gate)
		{
			if (_disposed || _observers == 0 || _inFlight)
			{
				return;
			}
		}

		StartRefresh();
	}

	private void StartRefresh()
	{
		CancellationToken token;
		lock (_gate)
		{
			if (_disposed || _inFlight)
			{
				return;
			}

			_inFlight = true;
			_requested = false;
			_lastStart = _time.GetUtcNow();
			_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			token = _disposal.Token;
		}

		_ = RefreshAsync(token);
	}

	private async Task RefreshAsync(CancellationToken cancellationToken)
	{
		try
		{
			var selectionVersion = Volatile.Read(ref _selectionVersion);
			var changes = await _reader.ReadChangesAsync(ProjectRoot, cancellationToken).ConfigureAwait(false);
			var files = changes?.Files.Select(entry => new ChangedFileViewModel(entry)).ToArray() ?? [];
			var selected = Reselect(_files, files, _selectedPath);
			var diff = selected is null
				? null
				: await _reader.ReadDiffAsync(ProjectRoot, selected.Entry, changes!.HeadCommit, cancellationToken)
					.ConfigureAwait(false);
			_postToUi(() => Apply(changes, files, selected, diff, selectionVersion));
		}
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
			or IOException or InvalidOperationException or UnauthorizedAccessException)
		{
			// The next reconciliation tries again.
		}
		finally
		{
			lock (_gate)
			{
				_inFlight = false;
				if (!_disposed && _observers > 0)
				{
					var next = _lastStart + (_requested ? MinRefreshInterval : ReconcileInterval);
					var wait = next - _time.GetUtcNow();
					_timer.Change(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
				}
			}
		}
	}

	private static ChangedFileViewModel? Reselect(
		ChangedFileViewModel[] previous,
		ChangedFileViewModel[] current,
		string? selectedPath)
	{
		if (selectedPath is null || current.Length == 0)
		{
			return null;
		}

		var kept = Array.Find(current, file => string.Equals(file.Path, selectedPath, StringComparison.Ordinal));
		if (kept is not null)
		{
			return kept;
		}

		var oldIndex = Array.FindIndex(previous, file => string.Equals(file.Path, selectedPath, StringComparison.Ordinal));
		return current[Math.Clamp(oldIndex, 0, current.Length - 1)];
	}

	private void Apply(
		WorkingTreeChanges? changes,
		ChangedFileViewModel[] files,
		ChangedFileViewModel? selected,
		FileDiff? diff,
		int selectionVersion)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
		}

		// The user picked a file while this read ran: their pick and its diff stand, and only the
		// list and the counts are taken from the read.
		var picked = Volatile.Read(ref _selectionVersion) != selectionVersion;
		IsAvailable = changes is not null;
		Branch = changes?.Branch ?? string.Empty;
		HeadCommit = changes?.HeadCommit;
		AddedLines = changes?.AddedLines ?? 0;
		RemovedLines = changes?.RemovedLines ?? 0;
		if (!files.Select(file => file.Entry).SequenceEqual(_files.Select(file => file.Entry)))
		{
			_files = files;
			Files = files;
		}
		else
		{
			selected = selected is null
				? null
				: Array.Find(_files, file => string.Equals(file.Path, selected.Path, StringComparison.Ordinal));
		}

		if (picked)
		{
			var path = _selectedPath;
			selected = Array.Find(_files, file => string.Equals(file.Path, path, StringComparison.Ordinal))
				?? SelectedFile;
		}
		else if (!string.Equals(selected?.Path, _selectedPath, StringComparison.Ordinal))
		{
			_showAll = false;
		}

		_selectedPath = selected?.Path;
		SelectedFile = selected;
		Raise(nameof(IsAvailable), nameof(Branch), nameof(HeadCommit), nameof(AddedLines), nameof(RemovedLines),
			nameof(Files), nameof(FileCount), nameof(SelectedFile), nameof(EntryTitle), nameof(EntrySubtitle),
			nameof(TabHeader));
		if (!picked)
		{
			ApplyDiff(diff);
		}
	}

	private void ApplyDiff(FileDiff? diff)
	{
		_selectedDiff = diff;
		List<DiffRowViewModel> rows = [];
		var shown = 0;
		var total = diff?.LineCount ?? 0;
		foreach (var hunk in diff?.Hunks ?? [])
		{
			if (!_showAll && shown >= MaxShownLines)
			{
				break;
			}

			rows.Add(new DiffRowViewModel(DiffRowKind.HunkHeader, string.Empty, string.Empty, HunkHeaderText(hunk)));
			foreach (var line in hunk.Lines)
			{
				if (!_showAll && shown >= MaxShownLines)
				{
					break;
				}

				rows.Add(new DiffRowViewModel(
					line.Kind switch
					{
						DiffLineKind.Added => DiffRowKind.Added,
						DiffLineKind.Removed => DiffRowKind.Removed,
						_ => DiffRowKind.Context
					},
					Format(line.OldNumber),
					Format(line.NewNumber),
					line.Text));
				shown++;
			}
		}

		if (!rows.SequenceEqual(DiffRows))
		{
			DiffRows = rows;
		}

		HiddenLineCount = total - shown;
		Raise(nameof(DiffRows), nameof(DiffPlaceholder), nameof(HiddenLineCount), nameof(ShowAllText));
	}

	private static string HunkHeaderText(DiffHunk hunk) => string.Create(
		CultureInfo.InvariantCulture,
		$"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@ {hunk.Header}").TrimEnd();

	private static string Format(int? number) =>
		number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

	private void Raise(params string[] names)
	{
		foreach (var name in names)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}

	private void Unobserve()
	{
		lock (_gate)
		{
			_observers--;
			if (_observers == 0 && !_disposed)
			{
				_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
			}
		}
	}

	private sealed class Observation(ProjectChangesViewModel owner) : IDisposable
	{
		private int _disposed;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				owner.Unobserve();
			}
		}
	}
}

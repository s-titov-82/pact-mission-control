using System.ComponentModel;
using System.Globalization;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.Services.Remarks;

namespace Pact.Presentation.ViewModels;

/// <summary>A session remarks can be sent to.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Title">Its tab title.</param>
public sealed record SessionTarget(string SessionId, string Title);

/// <summary>The threads placed on rows of one shown diff.</summary>
/// <param name="ForDiff">The diff the rows belong to.</param>
/// <param name="ByRow">Threads by row index of <see cref="ShownDiff.Rows"/>.</param>
public sealed record RowThreads(ShownDiff ForDiff, IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> ByRow);

/// <summary>
/// The remark threads of a project's Changes tab: where they sit on the shown diff, the ones that
/// do not, the counts, and sending the drafts to an agent. Markers are placed against the text the
/// shown diff was built from, never the file on disk, so they stay on the rows the user sees.
/// </summary>
public sealed class ChangesRemarksViewModel : INotifyPropertyChanged, IDisposable
{
	private const int ContextLines = 3;

	private readonly ProjectChangesViewModel _changes;
	private readonly RemarkBook _book;
	private readonly Func<IReadOnlyList<SessionTarget>> _sessions;
	private readonly Func<string, string, CancellationToken, Task<PromptDeliveryResult>> _deliver;
	private readonly Action<Action> _postToUi;
	private readonly Dictionary<string, RemarkThreadViewModel> _threads = new(StringComparer.Ordinal);
	private int _version;
	private bool _setAsideReported;
	private volatile bool _disposed;

	/// <summary>Creates the remarks of one project's Changes tab.</summary>
	/// <param name="changes">The project's change list.</param>
	/// <param name="book">The project's remarks.</param>
	/// <param name="sessions">The project's sessions, read when sending.</param>
	/// <param name="deliver">Delivers a prompt to a session.</param>
	/// <param name="postToUi">Runs an action on the UI thread.</param>
	public ChangesRemarksViewModel(
		ProjectChangesViewModel changes,
		RemarkBook book,
		Func<IReadOnlyList<SessionTarget>> sessions,
		Func<string, string, CancellationToken, Task<PromptDeliveryResult>> deliver,
		Action<Action> postToUi)
	{
		_changes = changes ?? throw new ArgumentNullException(nameof(changes));
		_book = book ?? throw new ArgumentNullException(nameof(book));
		_sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
		_deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
		_postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
		_changes.PropertyChanged += OnChangesPropertyChanged;
		_book.Changed += OnBookChanged;
		_ = LoadAsync();
		Recompute();
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>The shown file's threads on its rows; null while no diff is shown.</summary>
	public RowThreads? ThreadsByRow { get; private set; }

	/// <summary>Threads that are outdated or not on a shown row, newest first.</summary>
	public IReadOnlyList<RemarkThreadViewModel> Elsewhere { get; private set; } = [];

	/// <summary>Threads not resolved.</summary>
	public int OpenCount { get; private set; }

	/// <summary>Open threads with something to send.</summary>
	public int DraftCount { get; private set; }

	/// <summary><c>N remarks to send</c>, or empty when there is nothing to send.</summary>
	public string BarText => DraftCount switch
	{
		0 => string.Empty,
		1 => "1 remark to send",
		_ => string.Create(CultureInfo.InvariantCulture, $"{DraftCount} remarks to send")
	};

	/// <summary>The last thing worth telling about remarks, or null.</summary>
	public string? StatusText { get; private set; }

	/// <summary>The sessions remarks can go to.</summary>
	public IReadOnlyList<SessionTarget> SendTargets => _sessions();

	/// <summary>Whether there is something to send and somewhere to send it.</summary>
	public bool CanSend => DraftCount > 0 && SendTargets.Count > 0;

	/// <summary>Why sending is impossible, when it is for want of a session.</summary>
	public string? SendBlockedReason => SendTargets.Count == 0 ? "No session in this project to send to" : null;

	/// <summary>Whether a row of <paramref name="shown"/> takes a remark.</summary>
	public bool CanRemarkOn(ShownDiff shown, int row)
	{
		ArgumentNullException.ThrowIfNull(shown);
		if (!ReferenceEquals(shown, _changes.ShownDiff) || row < 0 || row >= shown.Rows.Count)
		{
			return false;
		}

		return shown.Rows[row].Kind switch
		{
			DiffRowKind.Removed => true,
			DiffRowKind.Added or DiffRowKind.Context => shown.NewLines is not null,
			_ => false
		};
	}

	/// <summary>
	/// Starts a thread on a row of the diff the user sees. A New-side remark keeps a copy of the
	/// text that diff was built from; the file on disk is not read, since it may have moved on.
	/// </summary>
	public async Task<RemarkThreadViewModel?> AddRemarkAsync(ShownDiff shown, int row, string text, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(shown);
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		if (!ReferenceEquals(shown, _changes.ShownDiff))
		{
			SetStatus("The diff changed; try again.");
			return null;
		}

		if (row < 0 || row >= shown.Rows.Count || shown.Rows[row].Kind == DiffRowKind.HunkHeader)
		{
			return null;
		}

		var target = shown.Rows[row];
		RemarkAnchor anchor;
		string? captured = null;
		if (target.Kind == DiffRowKind.Removed)
		{
			anchor = new RemarkAnchor(
				shown.Path,
				RemarkSide.Old,
				int.Parse(target.OldNumber, CultureInfo.InvariantCulture),
				target.Text,
				OldContext(shown.Rows, row, -1),
				OldContext(shown.Rows, row, 1),
				shown.HeadCommit,
				null);
		}
		else
		{
			if (shown.NewLines is not { } lines)
			{
				SetStatus("The file is changing; wait for the diff to refresh.");
				return null;
			}

			var index = int.Parse(target.NewNumber, CultureInfo.InvariantCulture) - 1;
			anchor = new RemarkAnchor(
				shown.Path,
				RemarkSide.New,
				index + 1,
				target.Text,
				lines.Skip(Math.Max(0, index - ContextLines)).Take(Math.Min(index, ContextLines)).ToArray(),
				lines.Skip(index + 1).Take(ContextLines).ToArray(),
				shown.HeadCommit,
				null);
			// A final newline, so the copy splits back into the same lines, a last blank line included.
			captured = lines.Count == 0 ? string.Empty : string.Join('\n', lines) + "\n";
		}

		var thread = await _book.AddAsync(anchor, captured, text, cancellationToken).ConfigureAwait(true);
		return ThreadFor(thread);
	}

	/// <summary>
	/// Sends every draft to a session in one prompt. A failure is reported, never thrown: before the
	/// delivery began the drafts stay drafts; after it, they are marked uncertain.
	/// </summary>
	public async Task SendAsync(string sessionId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		RemarkBatch? batch = null;
		var delivering = false;
		string? status;
		// A reply typed but not saved goes out too: Send means what is on screen.
		var typed = _threads.Values.Where(thread => thread.HasUnsavedDraft).ToArray();
		try
		{
			foreach (var thread in typed)
			{
				await thread.SaveDraftAsync(cancellationToken).ConfigureAwait(false);
			}

			batch = await _book.BeginSendAsync(sessionId, cancellationToken).ConfigureAwait(false);
			if (batch is null)
			{
				return;
			}

			var head = _changes.HeadCommit;
			List<RemarkPromptEntry> entries = [];
			foreach (var item in batch.Items)
			{
				var placement = await RemarkPlacer.PlaceOnDiskAsync(_book, item.Thread, head, cancellationToken).ConfigureAwait(false);
				entries.Add(new RemarkPromptEntry(item.Thread, item.Messages, placement.Line ?? item.Thread.Anchor.Line, placement.IsOutdated));
			}

			var prompt = RemarkPrompt.Compose(entries);
			delivering = true;
			var result = await _deliver(sessionId, prompt, cancellationToken).ConfigureAwait(false);
			await _book.CompleteSendAsync(batch, result, CancellationToken.None).ConfigureAwait(false);
			status = result.Outcome == PromptDeliveryOutcome.Unconfirmed
				? "Delivery was not confirmed."
				: PromptDeliveryText.DescribeUndelivered(result) is { } reason ? $"Remarks: {reason}." : null;
		}
		catch (Exception exception) when (batch is not null && exception is not OutOfMemoryException)
		{
			await _book.AbandonSendAsync(batch, delivering, CancellationToken.None).ConfigureAwait(false);
			status = delivering
				? "The remarks may not have arrived; use Send again or Mark as sent on each thread."
				: "Nothing was sent.";
		}

		_postToUi(() => SetStatus(status));
	}

	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_changes.PropertyChanged -= OnChangesPropertyChanged;
		_book.Changed -= OnBookChanged;
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.ShownDiff)
			or nameof(ProjectChangesViewModel.Files)
			or nameof(ProjectChangesViewModel.HeadCommit))
		{
			Recompute();
		}
	}

	private void OnBookChanged(object? sender, EventArgs e) => _postToUi(Recompute);

	// A failed read is reported; the remarks stay empty rather than silently broken.
	private async Task LoadAsync()
	{
		try
		{
			await _book.EnsureLoadedAsync(CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_postToUi(() => SetStatus($"The remarks could not be read: {exception.Message}"));
		}
	}

	// Open threads with something to send: a stored draft or a reply typed into the box.
	private int CountDrafts(IReadOnlyList<RemarkThread> threads) =>
		threads.Count(thread => !thread.IsResolved
			&& (thread.Unsent.Count > 0 || (_threads.TryGetValue(thread.Id, out var view) && view.HasUnsavedDraft)));

	private void OnThreadPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed || e.PropertyName != nameof(RemarkThreadViewModel.HasUnsavedDraft))
		{
			return;
		}

		var count = CountDrafts(_book.Threads);
		if (count != DraftCount)
		{
			DraftCount = count;
			Raise(nameof(DraftCount), nameof(BarText), nameof(CanSend));
		}
	}

	// On the UI thread: refreshes the thread view models and counts at once, then places the
	// threads off the UI thread and publishes the result only if nothing newer replaced it.
	private void Recompute()
	{
		if (_disposed)
		{
			return;
		}

		var version = ++_version;
		var threads = _book.Threads;
		foreach (var thread in threads)
		{
			ThreadFor(thread);
		}

		foreach (var gone in _threads.Keys.Except(threads.Select(thread => thread.Id), StringComparer.Ordinal).ToArray())
		{
			if (_threads.Remove(gone, out var removed))
			{
				removed.PropertyChanged -= OnThreadPropertyChanged;
			}
		}

		OpenCount = threads.Count(thread => !thread.IsResolved);
		DraftCount = CountDrafts(threads);
		foreach (var file in _changes.Files)
		{
			file.OpenRemarks = threads.Count(thread => !thread.IsResolved && string.Equals(thread.Anchor.Path, file.Path, StringComparison.Ordinal));
		}

		if (!_setAsideReported && _book.SetAsideFile is { } aside)
		{
			_setAsideReported = true;
			StatusText = $"The remarks file could not be read; it was kept as {Path.GetFileName(aside)}.";
		}

		Raise(nameof(OpenCount), nameof(DraftCount), nameof(BarText), nameof(CanSend), nameof(SendTargets),
			nameof(SendBlockedReason), nameof(StatusText));
		_ = PlaceAsync(version, threads, _changes.ShownDiff, _changes.HeadCommit);
	}

	private async Task PlaceAsync(int version, IReadOnlyList<RemarkThread> threads, ShownDiff? shown, string? head)
	{
		List<(RemarkThread Thread, RemarkPlacement Placement, int? Row)> placed = [];
		try
		{
			foreach (var thread in threads)
			{
				var onShown = shown is not null && string.Equals(thread.Anchor.Path, shown.Path, StringComparison.Ordinal);
				var placement = onShown
					? await RemarkPlacer.PlaceInTextAsync(_book, thread, shown!.NewLines, shown.HeadCommit, CancellationToken.None).ConfigureAwait(false)
					: await RemarkPlacer.PlaceOnDiskAsync(_book, thread, head, CancellationToken.None).ConfigureAwait(false);
				placed.Add((thread, placement, onShown && placement.Line is { } line ? RowOf(shown!, thread.Anchor.Side, line) : null));
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return;
		}

		_postToUi(() =>
		{
			if (_disposed || version != _version || !ReferenceEquals(shown, _changes.ShownDiff))
			{
				return;
			}

			Dictionary<int, IReadOnlyList<RemarkThreadViewModel>> byRow = [];
			List<RemarkThreadViewModel> elsewhere = [];
			foreach (var (thread, placement, row) in placed)
			{
				var view = ThreadFor(thread);
				view.SetPlacement(placement.Line, placement.IsOutdated);
				if (row is { } index)
				{
					byRow[index] = byRow.TryGetValue(index, out var existing) ? [.. existing, view] : [view];
				}
				else
				{
					elsewhere.Add(view);
				}
			}

			ThreadsByRow = shown is null ? null : new RowThreads(shown, byRow);
			Elsewhere = elsewhere.OrderByDescending(view => Number(view.Id)).ToArray();
			Raise(nameof(ThreadsByRow), nameof(Elsewhere));
		});
	}

	private RemarkThreadViewModel ThreadFor(RemarkThread thread)
	{
		if (_threads.TryGetValue(thread.Id, out var view))
		{
			if (!ReferenceEquals(view.Thread, thread))
			{
				view.Update(thread);
			}

			return view;
		}

		view = new RemarkThreadViewModel(_book, thread);
		view.PropertyChanged += OnThreadPropertyChanged;
		_threads[thread.Id] = view;
		return view;
	}

	private static int? RowOf(ShownDiff shown, RemarkSide side, int line)
	{
		var number = line.ToString(CultureInfo.InvariantCulture);
		for (var index = 0; index < shown.Rows.Count; index++)
		{
			var row = shown.Rows[index];
			var matches = side == RemarkSide.Old
				? row.Kind == DiffRowKind.Removed && row.OldNumber == number
				: row.Kind is DiffRowKind.Added or DiffRowKind.Context && row.NewNumber == number;
			if (matches)
			{
				return index;
			}
		}

		return null;
	}

	// The old-side lines next to a removed row, nearest to it first when walking away, then put in file order.
	private static string[] OldContext(IReadOnlyList<DiffRowViewModel> rows, int row, int step)
	{
		List<string> lines = [];
		for (var index = row + step; index >= 0 && index < rows.Count && lines.Count < ContextLines; index += step)
		{
			if (rows[index].Kind == DiffRowKind.HunkHeader)
			{
				break;
			}

			if (rows[index].OldNumber.Length > 0)
			{
				lines.Add(rows[index].Text);
			}
		}

		if (step < 0)
		{
			lines.Reverse();
		}

		return [.. lines];
	}

	private static int Number(string id) =>
		id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

	private void SetStatus(string? text)
	{
		StatusText = text;
		Raise(nameof(StatusText));
	}

	private void Raise(params string[] names)
	{
		foreach (var name in names)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}
}

using System.Globalization;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;

namespace Pact.Presentation.Services.Remarks;

/// <summary>What an agent's answer to a remark found.</summary>
/// <param name="Found">Whether the id named a thread of the project.</param>
/// <param name="OpenIds">The ids of the threads not resolved, for a refusal.</param>
public sealed record RemarkReply(bool Found, IReadOnlyList<string> OpenIds);

/// <summary>One thread's part of a delivery.</summary>
/// <param name="Thread">The thread with its batched messages marked Sending.</param>
/// <param name="Messages">The batched messages, oldest first.</param>
public sealed record RemarkBatchItem(RemarkThread Thread, IReadOnlyList<RemarkMessage> Messages);

/// <summary>The messages one delivery carries, as they were when it began.</summary>
/// <param name="SessionId">The session receiving them.</param>
/// <param name="Items">One item per thread.</param>
public sealed record RemarkBatch(string SessionId, IReadOnlyList<RemarkBatchItem> Items);

/// <summary>
/// One project's remark threads. The user's actions and the agents' MCP calls change it from any
/// thread; every change is saved before <see cref="Changed"/> is raised, on the caller's thread.
/// </summary>
public sealed class RemarkBook : IDisposable
{
	private readonly IRemarkStore _store;
	private readonly TimeProvider _time;
	private readonly SemaphoreSlim _gate = new(1, 1);
	private RemarkFile _file = new(1, []);
	private bool _loaded;

	/// <summary>Creates the book of one project; nothing is read until it is first used.</summary>
	public RemarkBook(string projectRoot, IRemarkStore store, TimeProvider time)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ProjectRoot = projectRoot;
		_store = store ?? throw new ArgumentNullException(nameof(store));
		_time = time ?? throw new ArgumentNullException(nameof(time));
	}

	/// <summary>Raised after a change is saved.</summary>
	public event EventHandler? Changed;

	/// <summary>The project root the remarks belong to.</summary>
	public string ProjectRoot { get; }

	/// <summary>Every thread, oldest id first.</summary>
	public IReadOnlyList<RemarkThread> Threads => Volatile.Read(ref _file).Threads;

	/// <summary>Where an unreadable remarks file was moved on load, or null.</summary>
	public string? SetAsideFile => Volatile.Read(ref _file).SetAsideFile;

	/// <summary>
	/// Reads the stored threads once. A message stored as Sending is taken as Uncertain: Pact
	/// stopped during its delivery, so it may or may not have reached the terminal.
	/// </summary>
	public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
	{
		if (Volatile.Read(ref _loaded))
		{
			return;
		}

		bool recovered;
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (_loaded)
			{
				return;
			}

			var file = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
			var threads = file.Threads
				.Select(thread => WithMessages(thread, message => message.Delivery == RemarkDelivery.Sending
					? message with { Delivery = RemarkDelivery.Uncertain }
					: message))
				.ToArray();
			recovered = !threads.SequenceEqual(file.Threads);
			file = file with { Threads = threads };
			if (recovered)
			{
				await _store.SaveAsync(file, cancellationToken).ConfigureAwait(false);
			}

			Volatile.Write(ref _file, file);
			Volatile.Write(ref _loaded, true);
		}
		finally
		{
			_gate.Release();
		}

		Changed?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Reads a stored copy of a file as lines, or null when it is missing.</summary>
	public Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string snapshotId, CancellationToken cancellationToken) =>
		_store.LoadSnapshotLinesAsync(snapshotId, cancellationToken);

	/// <summary>
	/// Starts a thread with the user's first message as a draft. A New-side anchor gets the copy of
	/// <paramref name="capturedText"/> it was written on.
	/// </summary>
	public async Task<RemarkThread> AddAsync(RemarkAnchor anchor, string? capturedText, string text, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(anchor);
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		if (anchor.Side == RemarkSide.New)
		{
			ArgumentNullException.ThrowIfNull(capturedText);
			anchor = anchor with { SnapshotId = await _store.SaveSnapshotAsync(capturedText, cancellationToken).ConfigureAwait(false) };
		}

		RemarkThread? added = null;
		await MutateAsync(
			file =>
			{
				added = new RemarkThread(
					string.Create(CultureInfo.InvariantCulture, $"C{file.NextNumber}"),
					anchor,
					[NewMessage(RemarkAuthor.User, text, RemarkDelivery.Draft)],
					IsResolved: false);
				return file with { NextNumber = file.NextNumber + 1, Threads = [.. file.Threads, added] };
			},
			cancellationToken).ConfigureAwait(false);
		return added!;
	}

	/// <summary>
	/// Replaces the text of the thread's newest unsent message, or adds one when it has none.
	/// Whitespace removes that message; a thread left without messages is removed, its id not reused.
	/// </summary>
	public Task SaveDraftAsync(string threadId, string text, CancellationToken cancellationToken) =>
		ChangeThreadAsync(
			threadId,
			thread =>
			{
				var draft = thread.EditableDraft;
				if (string.IsNullOrWhiteSpace(text))
				{
					return draft is null ? thread : thread with { Messages = thread.Messages.Where(message => message.Id != draft.Id).ToArray() };
				}

				return draft is null
					? thread with { Messages = [.. thread.Messages, NewMessage(RemarkAuthor.User, text, RemarkDelivery.Draft)] }
					: WithMessages(thread, message => message.Id == draft.Id ? message with { Text = text } : message);
			},
			cancellationToken);

	/// <summary>
	/// Saves the text of one draft box under the message id the box owns: into that message while it
	/// is unsent, as a new unsent message with that id when it is not stored yet, or with a fresh id
	/// when that message already went out. Never over another message. Whitespace removes the box's
	/// own unsent message, with the same rule for a thread left empty.
	/// </summary>
	/// <returns>The id of the stored unsent message, or null when nothing is stored.</returns>
	public async Task<string?> SaveDraftAsync(string threadId, string messageId, string text, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
		string? saved = null;
		await ChangeThreadAsync(
			threadId,
			thread =>
			{
				var existing = thread.Messages.FirstOrDefault(message => message.Id == messageId);
				var target = existing is { Delivery: RemarkDelivery.Draft } ? existing : null;
				if (string.IsNullOrWhiteSpace(text))
				{
					return target is null ? thread : thread with { Messages = thread.Messages.Where(message => message.Id != target.Id).ToArray() };
				}

				if (target is not null)
				{
					saved = target.Id;
					return WithMessages(thread, message => message.Id == target.Id ? message with { Text = text } : message);
				}

				var added = NewMessage(RemarkAuthor.User, text, RemarkDelivery.Draft) with
				{
					Id = existing is null ? messageId : Guid.NewGuid().ToString("N")
				};
				saved = added.Id;
				return thread with { Messages = [.. thread.Messages, added] };
			},
			cancellationToken).ConfigureAwait(false);
		return saved;
	}

	/// <summary>Removes one unsent message, with the same rule for a thread left empty.</summary>
	public Task DiscardUnsentAsync(string threadId, string messageId, CancellationToken cancellationToken) =>
		ChangeThreadAsync(
			threadId,
			thread => thread with
			{
				Messages = thread.Messages
					.Where(message => !(message.Id == messageId && message.Delivery == RemarkDelivery.Draft))
					.ToArray()
			},
			cancellationToken);

	/// <summary>Marks a thread resolved.</summary>
	public Task ResolveAsync(string threadId, CancellationToken cancellationToken) =>
		ChangeThreadAsync(threadId, thread => thread with { IsResolved = true }, cancellationToken);

	/// <summary>Opens a resolved thread again.</summary>
	public Task ReopenAsync(string threadId, CancellationToken cancellationToken) =>
		ChangeThreadAsync(threadId, thread => thread with { IsResolved = false }, cancellationToken);

	/// <summary>Clears the project's entirely resolved list only when its complete captured snapshot still matches.</summary>
	public async Task<bool> TryClearReviewAsync(IReadOnlyList<RemarkThread> expected, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expected);
		var snapshot = expected.ToArray();
		var cleared = false;
		await MutateAsync(file =>
		{
			var current = file.Threads;
			if (current.Count == 0 || !current.SequenceEqual(snapshot) || current.Any(thread => !thread.IsResolved
				|| thread.Messages.Any(message => message.Delivery != RemarkDelivery.Sent)))
			{ return null; }
			cleared = true;
			return file with { Threads = [] };
		}, cancellationToken).ConfigureAwait(false);
		return cleared;
	}

	/// <summary>
	/// Appends an agent's answer. <paramref name="resolve"/> resolves the thread; an answer without it
	/// leaves the thread open, reopening it if it was resolved.
	/// </summary>
	public async Task<RemarkReply> AgentReplyAsync(string threadId, string text, bool resolve, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
		ArgumentException.ThrowIfNullOrWhiteSpace(text);
		var found = false;
		IReadOnlyList<string> open = [];
		await MutateAsync(
			file =>
			{
				open = file.Threads.Where(thread => !thread.IsResolved).Select(thread => thread.Id).ToArray();
				if (!file.Threads.Any(thread => string.Equals(thread.Id, threadId, StringComparison.OrdinalIgnoreCase)))
				{
					return null;
				}

				found = true;
				return ReplaceThread(
					file,
					threadId,
					thread => thread with
					{
						Messages = [.. thread.Messages, NewMessage(RemarkAuthor.Agent, text, RemarkDelivery.Sent)],
						IsResolved = resolve
					});
			},
			cancellationToken).ConfigureAwait(false);
		return new RemarkReply(found, found ? [] : open);
	}

	/// <summary>
	/// Marks unsent messages of every open project thread as Sending to
	/// <paramref name="sessionId"/>, regardless of the displayed file or commit.
	/// </summary>
	public async Task<RemarkBatch?> BeginSendAsync(string sessionId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		List<RemarkBatchItem> items = [];
		await MutateAsync(
			file =>
			{
				var threads = file.Threads
					.Select(thread =>
					{
						if (thread.IsResolved || thread.Unsent.Count == 0)
						{
							return thread;
						}

						var ids = thread.Unsent.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
						var sending = WithMessages(thread, message => ids.Contains(message.Id)
							? message with { Delivery = RemarkDelivery.Sending, SessionId = sessionId }
							: message);
						items.Add(new RemarkBatchItem(sending, sending.Messages.Where(message => ids.Contains(message.Id)).ToArray()));
						return sending;
					})
					.ToArray();
				return items.Count == 0 ? null : file with { Threads = threads };
			},
			cancellationToken).ConfigureAwait(false);
		return items.Count == 0 ? null : new RemarkBatch(sessionId, items);
	}

	/// <summary>
	/// Records how a delivery ended, for the batch's messages that are still Sending: submitted →
	/// Sent; nothing written → back to Draft in place; written but not submitted → Uncertain.
	/// </summary>
	public Task CompleteSendAsync(RemarkBatch batch, PromptDeliveryResult result, CancellationToken cancellationToken)
	{
		var delivery = result.IsSent
			? RemarkDelivery.Sent
			: result.WriteAttempted ? RemarkDelivery.Uncertain : RemarkDelivery.Draft;
		return FinishBatchAsync(batch, delivery, cancellationToken);
	}

	/// <summary>Ends a batch whose send failed by an exception: Draft when nothing can have been written, else Uncertain.</summary>
	public Task AbandonSendAsync(RemarkBatch batch, bool mayHaveWritten, CancellationToken cancellationToken) =>
		FinishBatchAsync(batch, mayHaveWritten ? RemarkDelivery.Uncertain : RemarkDelivery.Draft, cancellationToken);

	/// <summary>Turns every uncertain message of a thread back into an unsent one, in place.</summary>
	public Task RetryAsync(string threadId, CancellationToken cancellationToken) =>
		ChangeThreadAsync(
			threadId,
			thread => WithMessages(thread, message => message.Delivery == RemarkDelivery.Uncertain
				? message with { Delivery = RemarkDelivery.Draft, SessionId = null }
				: message),
			cancellationToken);

	/// <summary>Takes the uncertain messages of a thread as sent.</summary>
	public Task MarkSentAsync(string threadId, CancellationToken cancellationToken) =>
		ChangeThreadAsync(
			threadId,
			thread => WithMessages(thread, message => message.Delivery == RemarkDelivery.Uncertain
				? message with { Delivery = RemarkDelivery.Sent }
				: message),
			cancellationToken);

	private Task FinishBatchAsync(RemarkBatch batch, RemarkDelivery delivery, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(batch);
		var ids = batch.Items.SelectMany(item => item.Messages).Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
		return MutateAsync(
			file => file with
			{
				Threads = file.Threads
					.Select(thread => WithMessages(thread, message => ids.Contains(message.Id) && message.Delivery == RemarkDelivery.Sending
						? message with
						{
							Delivery = delivery,
							SessionId = delivery == RemarkDelivery.Draft ? null : message.SessionId
						}
						: message))
					.ToArray()
			},
			cancellationToken);
	}

	private Task ChangeThreadAsync(string threadId, Func<RemarkThread, RemarkThread> change, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
		return MutateAsync(file => ReplaceThread(file, threadId, change), cancellationToken);
	}

	private static RemarkFile? ReplaceThread(RemarkFile file, string threadId, Func<RemarkThread, RemarkThread> change)
	{
		var index = file.Threads.ToList().FindIndex(thread => string.Equals(thread.Id, threadId, StringComparison.OrdinalIgnoreCase));
		if (index < 0)
		{
			return null;
		}

		var changed = change(file.Threads[index]);
		var threads = file.Threads.ToList();
		if (changed.Messages.Count == 0)
		{
			threads.RemoveAt(index);
		}
		else
		{
			threads[index] = changed;
		}

		return file with { Threads = threads };
	}

	private static RemarkThread WithMessages(RemarkThread thread, Func<RemarkMessage, RemarkMessage> change)
	{
		var messages = thread.Messages.Select(change).ToArray();
		return messages.SequenceEqual(thread.Messages) ? thread : thread with { Messages = messages };
	}

	// Applies a change under the gate and saves it; null means nothing changed. Changed is raised
	// after the gate is released, so a handler may call back into the book.
	private async Task MutateAsync(Func<RemarkFile, RemarkFile?> change, CancellationToken cancellationToken)
	{
		await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		RemarkFile? changed;
		try
		{
			changed = change(_file);
			if (changed is not null)
			{
				await _store.SaveAsync(changed, cancellationToken).ConfigureAwait(false);
				Volatile.Write(ref _file, changed);
			}
		}
		finally
		{
			_gate.Release();
		}

		if (changed is not null)
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <inheritdoc />
	public void Dispose() => _gate.Dispose();

	private RemarkMessage NewMessage(RemarkAuthor author, string text, RemarkDelivery delivery) =>
		new(Guid.NewGuid().ToString("N"), author, text, _time.GetUtcNow(), delivery, null);
}
using System.ComponentModel;
using System.Globalization;
using Pact.Core.Remarks;
using Pact.Presentation.Services.Remarks;

namespace Pact.Presentation.ViewModels;

/// <summary>One message of a remark thread as shown.</summary>
/// <param name="Id">The message id, for editing or removing an unsent one.</param>
/// <param name="AuthorText"><c>You</c> or <c>Agent</c>.</param>
/// <param name="Text">The message.</param>
/// <param name="DeliveryText">How far a message of the user got, when it is not simply sent.</param>
/// <param name="IsUnsent">Whether it waits for the next send and can still be edited or removed.</param>
public sealed record RemarkMessageViewModel(string Id, string AuthorText, string Text, string? DeliveryText, bool IsUnsent);

/// <summary>
/// One remark thread in the Changes tab. One instance lives as long as its thread, so the draft
/// the user is typing survives every refresh of the thread.
/// </summary>
public sealed class RemarkThreadViewModel : INotifyPropertyChanged
{
	private readonly RemarkBook _book;
	private string _storedDraft = string.Empty;

	// The id of the message the reply box owns: an unsent message put back in it with the pencil, or
	// an id reserved for the next reply, which the book stores under it. Never inferred from text.
	private string _draftId = NewDraftId();
	private int? _placedLine;

	/// <summary>Wraps a thread of <paramref name="book"/>.</summary>
	public RemarkThreadViewModel(RemarkBook book, RemarkThread thread)
	{
		_book = book ?? throw new ArgumentNullException(nameof(book));
		Thread = thread ?? throw new ArgumentNullException(nameof(thread));
		Update(thread);
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>The thread as last seen.</summary>
	public RemarkThread Thread { get; private set; }

	/// <summary>The thread id, such as <c>C3</c>.</summary>
	public string Id => Thread.Id;

	/// <summary>Where the thread stands.</summary>
	public RemarkThreadState State => Thread.State;

	/// <summary>The state in words.</summary>
	public string StateText => State switch
	{
		RemarkThreadState.Draft => "Draft",
		RemarkThreadState.Sent => "Sent",
		RemarkThreadState.DeliveryUncertain => "Delivery uncertain",
		RemarkThreadState.Answered => "Answered",
		_ => "Resolved"
	};

	/// <summary>Whether the line no longer matches the file.</summary>
	public bool IsOutdated { get; private set; }

	/// <summary>The file and line, such as <c>src/X.cs:121</c> or <c>src/X.cs, removed line 121</c>.</summary>
	public string LocationText => Thread.Anchor.Side == RemarkSide.Old
		? string.Create(CultureInfo.InvariantCulture, $"{Thread.Anchor.Path}, removed line {Thread.Anchor.Line}")
		: string.Create(CultureInfo.InvariantCulture, $"{Thread.Anchor.Path}:{_placedLine ?? Thread.Anchor.Line}");

	/// <summary>The remarked line as the user saw it.</summary>
	public string LineText => Thread.Anchor.LineText;

	/// <summary>Every message but the one in the draft box, oldest first.</summary>
	public IReadOnlyList<RemarkMessageViewModel> Messages { get; private set; } = [];

	/// <summary>The text of the draft box: the newest unsent message.</summary>
	public string DraftText
	{
		get;
		set
		{
			if (!string.Equals(field, value, StringComparison.Ordinal))
			{
				field = value ?? string.Empty;
				Raise(nameof(DraftText), nameof(HasUnsavedDraft));
			}
		}
	} = string.Empty;

	/// <summary>Whether the draft box holds text that is not stored yet.</summary>
	public bool HasUnsavedDraft => !string.Equals(DraftText, _storedDraft, StringComparison.Ordinal);

	/// <summary>Whether the thread can be resolved.</summary>
	public bool CanResolve => !Thread.IsResolved;

	/// <summary>Whether the thread can be opened again.</summary>
	public bool CanReopen => Thread.IsResolved;

	/// <summary>Whether a message waits for <c>Send again</c> or <c>Mark as sent</c>.</summary>
	public bool CanRetry => State == RemarkThreadState.DeliveryUncertain;

	/// <summary>
	/// Adds the reply box's text to the thread as an unsent message, or stores the edit of the one
	/// put back with the pencil, then empties the box for the next reply. Text typed while it was
	/// being stored stays in the box as that next reply.
	/// </summary>
	public async Task SaveDraftAsync(CancellationToken cancellationToken)
	{
		var owned = _draftId;
		var text = DraftText;
		if (string.IsNullOrWhiteSpace(text) && !IsEditing)
		{
			return;
		}

		await _book.SaveDraftAsync(Id, owned, text, cancellationToken).ConfigureAwait(true);
		if (_draftId != owned)
		{
			return;
		}

		_draftId = NewDraftId();
		_storedDraft = string.Empty;
		if (string.Equals(DraftText, text, StringComparison.Ordinal))
		{
			DraftText = string.Empty;
		}

		Refresh();
	}

	/// <summary>Empties the reply box; an unsent message put back with the pencil stays as it was.</summary>
	public Task DiscardDraftAsync(CancellationToken cancellationToken)
	{
		_draftId = NewDraftId();
		_storedDraft = string.Empty;
		DraftText = string.Empty;
		Refresh();
		return Task.CompletedTask;
	}

	/// <summary>Puts an unsent message back in the reply box to edit it; text typed there first is kept as a reply.</summary>
	public void EditMessage(string messageId)
	{
		if (Thread.Messages.FirstOrDefault(message => message.Id == messageId) is not { Author: RemarkAuthor.User, Delivery: RemarkDelivery.Draft } message)
		{
			return;
		}

		if (HasUnsavedDraft && !string.IsNullOrWhiteSpace(DraftText))
		{
			_ = _book.SaveDraftAsync(Id, _draftId, DraftText, CancellationToken.None);
		}

		_draftId = message.Id;
		_storedDraft = message.Text;
		DraftText = message.Text;
		Refresh();
	}

	/// <summary>Removes an unsent message before it is sent.</summary>
	public async Task RemoveMessageAsync(string messageId, CancellationToken cancellationToken)
	{
		if (messageId == _draftId)
		{
			_draftId = NewDraftId();
			_storedDraft = string.Empty;
			DraftText = string.Empty;
		}

		await _book.DiscardUnsentAsync(Id, messageId, cancellationToken).ConfigureAwait(true);
		Refresh();
	}

	// Whether the reply box holds an unsent message put back with the pencil.
	private bool IsEditing => Thread.Messages.Any(message => message.Id == _draftId && message.Delivery == RemarkDelivery.Draft);

	private void Refresh()
	{
		if (_book.Threads.FirstOrDefault(thread => thread.Id == Id) is { } current)
		{
			Update(current);
		}
	}

	/// <summary>Resolves the thread.</summary>
	public Task ResolveAsync(CancellationToken cancellationToken) => _book.ResolveAsync(Id, cancellationToken);

	/// <summary>Opens the thread again.</summary>
	public Task ReopenAsync(CancellationToken cancellationToken) => _book.ReopenAsync(Id, cancellationToken);

	/// <summary>Puts the uncertain messages back among the unsent ones.</summary>
	public Task RetryAsync(CancellationToken cancellationToken) => _book.RetryAsync(Id, cancellationToken);

	/// <summary>Takes the uncertain messages as sent.</summary>
	public Task MarkSentAsync(CancellationToken cancellationToken) => _book.MarkSentAsync(Id, cancellationToken);

	/// <summary>
	/// Takes a newer copy of the thread. Unsent messages show in the list; the reply box keeps what
	/// was typed into it and the message it owns, so a message a failed delivery hands back stays
	/// a separate one.
	/// </summary>
	public void Update(RemarkThread thread)
	{
		ArgumentNullException.ThrowIfNull(thread);
		Thread = thread;
		var owned = thread.Messages.FirstOrDefault(message => message.Id == _draftId);
		if (owned is { Delivery: RemarkDelivery.Draft })
		{
			_storedDraft = owned.Text;
		}
		else
		{
			if (owned is not null)
			{
				// The message it edited went out: the box is for the next reply.
				_draftId = NewDraftId();
			}

			_storedDraft = string.Empty;
		}

		var editing = _draftId;
		Messages = thread.Messages
			.Where(message => !(message.Id == editing && message.Delivery == RemarkDelivery.Draft))
			.Select(message => new RemarkMessageViewModel(
				message.Id,
				message.Author == RemarkAuthor.Agent ? "Agent" : "You",
				message.Text,
				message.Author == RemarkAuthor.Agent ? null : message.Delivery switch
				{
					RemarkDelivery.Draft => "Not sent yet",
					RemarkDelivery.Sending => "Sending…",
					RemarkDelivery.Uncertain => "Delivery uncertain",
					_ => null
				},
				message is { Author: RemarkAuthor.User, Delivery: RemarkDelivery.Draft }))
			.ToArray();
		Raise(nameof(Thread), nameof(State), nameof(StateText), nameof(Messages), nameof(CanResolve), nameof(CanReopen), nameof(HasUnsavedDraft),
			nameof(CanRetry), nameof(LocationText));
	}

	private static string NewDraftId() => Guid.NewGuid().ToString("N");

	/// <summary>Records where the thread was placed.</summary>
	public void SetPlacement(int? line, bool isOutdated)
	{
		_placedLine = isOutdated ? null : line;
		if (IsOutdated != isOutdated)
		{
			IsOutdated = isOutdated;
			Raise(nameof(IsOutdated));
		}

		Raise(nameof(LocationText));
	}

	private void Raise(params string[] names)
	{
		foreach (var name in names)
		{
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
		}
	}
}

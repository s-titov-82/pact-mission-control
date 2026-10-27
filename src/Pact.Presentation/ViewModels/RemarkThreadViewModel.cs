using System.ComponentModel;
using System.Globalization;
using Pact.Core.Remarks;
using Pact.Presentation.Services.Remarks;

namespace Pact.Presentation.ViewModels;

/// <summary>One message of a remark thread as shown.</summary>
/// <param name="AuthorText"><c>You</c> or <c>Agent</c>.</param>
/// <param name="Text">The message.</param>
/// <param name="DeliveryText">How far a message of the user got, when it is not simply sent.</param>
public sealed record RemarkMessageViewModel(string AuthorText, string Text, string? DeliveryText);

/// <summary>
/// One remark thread in the Changes tab. One instance lives as long as its thread, so the draft
/// the user is typing survives every refresh of the thread.
/// </summary>
public sealed class RemarkThreadViewModel : INotifyPropertyChanged
{
	private readonly RemarkBook _book;
	private string _storedDraft = string.Empty;

	// The id of the message the draft box owns: an unsent message it edits, or an id reserved for a
	// new reply that the book stores under it. Ownership is never inferred from text.
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

	/// <summary>Saves the draft box under the message id it owns; empty text removes that message.</summary>
	public async Task SaveDraftAsync(CancellationToken cancellationToken)
	{
		var owned = _draftId;
		var saved = await _book.SaveDraftAsync(Id, owned, DraftText, cancellationToken).ConfigureAwait(true);
		if (saved is not null && saved != owned && _draftId == owned)
		{
			// The owned message had gone out meanwhile; the book stored the text under a fresh id.
			_draftId = saved;
			if (_book.Threads.FirstOrDefault(thread => thread.Id == Id) is { } current)
			{
				Update(current);
			}
		}
	}

	/// <summary>Empties the draft box and removes the message it edits; a new reply only empties the box.</summary>
	public Task DiscardDraftAsync(CancellationToken cancellationToken)
	{
		DraftText = string.Empty;
		return _book.SaveDraftAsync(Id, _draftId, string.Empty, cancellationToken);
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
	/// Takes a newer copy of the thread. An untouched draft box follows the newest unsent message;
	/// one the user typed into keeps its text and the message it edits, so a message a failed
	/// delivery hands back stays a separate, older one.
	/// </summary>
	public void Update(RemarkThread thread)
	{
		ArgumentNullException.ThrowIfNull(thread);
		Thread = thread;
		var owned = thread.Messages.FirstOrDefault(message => message.Id == _draftId);
		if (!HasUnsavedDraft)
		{
			if (thread.EditableDraft is { } newest)
			{
				_draftId = newest.Id;
				_storedDraft = newest.Text;
			}
			else
			{
				if (owned is not null)
				{
					_draftId = NewDraftId();
				}

				_storedDraft = string.Empty;
			}

			DraftText = _storedDraft;
		}
		else if (owned is { Delivery: RemarkDelivery.Draft })
		{
			_storedDraft = owned.Text;
		}
		else if (owned is not null)
		{
			// The message it edited went out: the typed text becomes a new reply.
			_draftId = NewDraftId();
			_storedDraft = string.Empty;
		}
		else
		{
			// Not stored yet, or removed: the typed text is a new reply under the owned id.
			_storedDraft = string.Empty;
		}

		var editing = _draftId;
		Messages = thread.Messages
			.Where(message => !(message.Id == editing && message.Delivery == RemarkDelivery.Draft))
			.Select(message => new RemarkMessageViewModel(
				message.Author == RemarkAuthor.Agent ? "Agent" : "You",
				message.Text,
				message.Author == RemarkAuthor.Agent ? null : message.Delivery switch
				{
					RemarkDelivery.Draft => "Not sent yet",
					RemarkDelivery.Sending => "Sending…",
					RemarkDelivery.Uncertain => "Delivery uncertain",
					_ => null
				}))
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

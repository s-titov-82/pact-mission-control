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
				Raise(nameof(DraftText));
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

	/// <summary>Saves the draft box; empty text removes the draft.</summary>
	public Task SaveDraftAsync(CancellationToken cancellationToken) =>
		_book.SaveDraftAsync(Id, DraftText, cancellationToken);

	/// <summary>Empties the draft box and removes the draft.</summary>
	public Task DiscardDraftAsync(CancellationToken cancellationToken)
	{
		DraftText = string.Empty;
		return _book.SaveDraftAsync(Id, string.Empty, cancellationToken);
	}

	/// <summary>Resolves the thread.</summary>
	public Task ResolveAsync(CancellationToken cancellationToken) => _book.ResolveAsync(Id, cancellationToken);

	/// <summary>Opens the thread again.</summary>
	public Task ReopenAsync(CancellationToken cancellationToken) => _book.ReopenAsync(Id, cancellationToken);

	/// <summary>Puts the uncertain messages back among the unsent ones.</summary>
	public Task RetryAsync(CancellationToken cancellationToken) => _book.RetryAsync(Id, cancellationToken);

	/// <summary>Takes the uncertain messages as sent.</summary>
	public Task MarkSentAsync(CancellationToken cancellationToken) => _book.MarkSentAsync(Id, cancellationToken);

	/// <summary>Takes a newer copy of the thread; the draft box changes only while it still shows the stored draft.</summary>
	public void Update(RemarkThread thread)
	{
		ArgumentNullException.ThrowIfNull(thread);
		Thread = thread;
		var draft = thread.EditableDraft;
		var stored = draft?.Text ?? string.Empty;
		if (string.Equals(DraftText, _storedDraft, StringComparison.Ordinal))
		{
			DraftText = stored;
		}

		_storedDraft = stored;
		Messages = thread.Messages
			.Where(message => draft is null || message.Id != draft.Id)
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
		Raise(nameof(Thread), nameof(State), nameof(StateText), nameof(Messages), nameof(CanResolve), nameof(CanReopen),
			nameof(CanRetry), nameof(LocationText));
	}

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

using System.Text.Json.Serialization;

namespace Pact.Core.Remarks;

/// <summary>Which version of a file a remark's line belongs to.</summary>
public enum RemarkSide
{
	/// <summary>An added or unchanged line of the working tree or historical target.</summary>
	New,

	/// <summary>A removed line of the commit the changes were compared with.</summary>
	Old
}

/// <summary>Where a remark points: the line as the user saw it, with a little context.</summary>
/// <param name="Path">Repository-relative path with forward slashes.</param>
/// <param name="Side">Whether the line belongs to the new version or was removed from the base.</param>
/// <param name="Line">1-based line in the captured revision (New) or in <paramref name="BaseCommit"/> (Old).</param>
/// <param name="LineText">The line's text when the remark was written.</param>
/// <param name="Before">Up to three lines above it, nearest last.</param>
/// <param name="After">Up to three lines below it, nearest first.</param>
/// <param name="BaseCommit">The commit the changes were compared with; <see langword="null"/> for the empty tree.</param>
/// <param name="SnapshotId">The stored copy of the file as it was when written; New side only.</param>
/// <param name="TargetCommit">Full immutable historical target; null preserves working-tree semantics.</param>
/// <param name="OriginalPath">Old path for a rename, used with the comparison-base revision.</param>
public sealed record RemarkAnchor(
	string Path,
	RemarkSide Side,
	int Line,
	string LineText,
	IReadOnlyList<string> Before,
	IReadOnlyList<string> After,
	string? BaseCommit,
	string? SnapshotId,
	string? TargetCommit = null,
	string? OriginalPath = null);

/// <summary>Who wrote a message of a remark thread.</summary>
public enum RemarkAuthor
{
	/// <summary>The user, in Pact.</summary>
	User,

	/// <summary>An agent, over MCP.</summary>
	Agent
}

/// <summary>How far a user's message got towards the agent; agent messages are always Sent.</summary>
public enum RemarkDelivery
{
	/// <summary>Written, not sent yet.</summary>
	Draft,

	/// <summary>Part of a delivery that has not ended.</summary>
	Sending,

	/// <summary>Delivered to an agent.</summary>
	Sent,

	/// <summary>It may have reached the terminal, but nothing says it was submitted.</summary>
	Uncertain
}

/// <summary>One message of a remark thread.</summary>
/// <param name="Id">Stable id, so a delivery result finds its message.</param>
/// <param name="Author">Who wrote it.</param>
/// <param name="Text">The message.</param>
/// <param name="At">When it was written.</param>
/// <param name="Delivery">How far it got.</param>
/// <param name="SessionId">The session it was sent to; <see langword="null"/> while a draft and for agent messages.</param>
public sealed record RemarkMessage(
	string Id,
	RemarkAuthor Author,
	string Text,
	DateTimeOffset At,
	RemarkDelivery Delivery,
	string? SessionId);

/// <summary>Where a thread stands, derived from its messages.</summary>
public enum RemarkThreadState
{
	/// <summary>A message waits to be sent.</summary>
	Draft,

	/// <summary>Delivered, waiting for the agent.</summary>
	Sent,

	/// <summary>A message may have reached the terminal without being submitted.</summary>
	DeliveryUncertain,

	/// <summary>The agent replied without resolving.</summary>
	Answered,

	/// <summary>The agent or the user resolved it.</summary>
	Resolved
}

/// <summary>
/// A remark on a diff line and the conversation about it. Delivery lives on the messages and the
/// state is derived from them, so a late delivery result never undoes an answer.
/// </summary>
/// <param name="Id">Project-wide id such as <c>C3</c>, never reused.</param>
/// <param name="Anchor">The line the remark is about.</param>
/// <param name="Messages">Messages, oldest first.</param>
/// <param name="IsResolved">Whether the agent or the user resolved it.</param>
public sealed record RemarkThread(
	string Id,
	RemarkAnchor Anchor,
	IReadOnlyList<RemarkMessage> Messages,
	bool IsResolved)
{
	/// <summary>Where the thread stands.</summary>
	[JsonIgnore]
	public RemarkThreadState State
	{
		get
		{
			if (IsResolved)
			{
				return RemarkThreadState.Resolved;
			}

			var lastUncertain = LastIndexOf(message => message.Author == RemarkAuthor.User && message.Delivery == RemarkDelivery.Uncertain);
			var lastAgent = LastIndexOf(message => message.Author == RemarkAuthor.Agent);
			if (lastUncertain > lastAgent)
			{
				return RemarkThreadState.DeliveryUncertain;
			}

			if (Unsent.Count > 0)
			{
				return RemarkThreadState.Draft;
			}

			return Messages.Count > 0 && Messages[^1].Author == RemarkAuthor.Agent
				? RemarkThreadState.Answered
				: RemarkThreadState.Sent;
		}
	}

	/// <summary>
	/// Every user message not sent yet, oldest first. A failed delivery hands its message back
	/// while a newer one may already be typed; all of them go out with the next send.
	/// </summary>
	[JsonIgnore]
	public IReadOnlyList<RemarkMessage> Unsent =>
		Messages.Where(message => message.Author == RemarkAuthor.User && message.Delivery == RemarkDelivery.Draft).ToArray();

	/// <summary>The newest unsent message, the one the draft box edits.</summary>
	[JsonIgnore]
	public RemarkMessage? EditableDraft => Unsent.Count == 0 ? null : Unsent[^1];

	/// <summary>The session the newest sent user message went to.</summary>
	[JsonIgnore]
	public string? LastSessionId =>
		Messages.LastOrDefault(message => message.Author == RemarkAuthor.User && message.Delivery == RemarkDelivery.Sent)?.SessionId;

	private int LastIndexOf(Func<RemarkMessage, bool> match)
	{
		for (var index = Messages.Count - 1; index >= 0; index--)
		{
			if (match(Messages[index]))
			{
				return index;
			}
		}

		return -1;
	}
}
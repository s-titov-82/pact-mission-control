using System.Globalization;
using System.Text;

namespace Pact.Core.Remarks;

/// <summary>One thread's part of a remark prompt.</summary>
/// <param name="Thread">The thread, holding <paramref name="NewMessages"/> among its messages.</param>
/// <param name="NewMessages">The user's messages this prompt delivers, oldest first.</param>
/// <param name="Line">The line to name: where the anchor is now, or where it was when it cannot be placed.</param>
/// <param name="IsOutdated">Whether the anchor no longer matches the file.</param>
public sealed record RemarkPromptEntry(RemarkThread Thread, IReadOnlyList<RemarkMessage> NewMessages, int Line, bool IsOutdated);

/// <summary>Writes the one prompt that carries every remark the user is sending to an agent.</summary>
public static class RemarkPrompt
{
	private const string Header =
		"Remarks from the user on the uncommitted changes. Answer every id with pact_reply_remark (id, text; resolve: true when it is done); pact_list_remarks shows them again.";

	/// <summary>
	/// A header, then one block per thread: a head line with the id, the place and the text (or
	/// <c>reply</c>), the remarked line quoted, and for a reply the conversation so far.
	/// </summary>
	public static string Compose(IReadOnlyList<RemarkPromptEntry> entries)
	{
		ArgumentNullException.ThrowIfNull(entries);
		StringBuilder text = new(Header);
		foreach (var entry in entries)
		{
			text.Append("\n\n");
			AppendBlock(text, entry);
		}

		return text.ToString();
	}

	private static void AppendBlock(StringBuilder text, RemarkPromptEntry entry)
	{
		var anchor = entry.Thread.Anchor;
		var newIds = entry.NewMessages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
		var earlier = entry.Thread.Messages
			.Where(message => !newIds.Contains(message.Id) && message.Delivery != RemarkDelivery.Draft)
			.ToArray();
		var first = earlier.Length == 0 && entry.NewMessages.Count == 1;

		text.Append(entry.Thread.Id);
		if (entry.IsOutdated)
		{
			text.Append(" (outdated)");
		}

		text.Append(" · ").Append(Location(anchor, entry.Line)).Append(" · ");
		text.Append(first ? Indent(entry.NewMessages[0].Text) : "reply");
		text.Append("\n> ").Append(anchor.LineText);
		if (first)
		{
			return;
		}

		foreach (var message in earlier.Concat(entry.NewMessages))
		{
			text.Append('\n')
				.Append(message.Author == RemarkAuthor.Agent ? "agent: " : "you: ")
				.Append(Indent(message.Text));
		}
	}

	private static string Location(RemarkAnchor anchor, int line)
	{
		if (anchor.Side == RemarkSide.New)
		{
			return string.Create(CultureInfo.InvariantCulture, $"{anchor.Path}:{line}");
		}

		var commit = anchor.BaseCommit is { } id
			? $"HEAD {(id.Length > 7 ? id[..7] : id)}"
			: "the empty tree";
		return string.Create(CultureInfo.InvariantCulture, $"{anchor.Path}, removed line {line} of {commit}");
	}

	// Lines after the first are indented, so a message never reads as a new head line.
	private static string Indent(string message) =>
		string.Join("\n  ", message.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Remarks;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.Remarks;
using static Pact.Presentation.Services.AgentControl.AgentControlToolArguments;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>
/// The tools an agent answers the user's remark threads with. Any session of the project may answer;
/// the remarks are the project's, not one session's.
/// </summary>
public sealed class RemarkTools
{
	private static readonly string[] States = ["open", "draft", "sent", "uncertain", "answered", "resolved", "all"];

	private readonly AgentControlDispatcher _dispatcher;
	private readonly RemarkBooks _books;
	private readonly Func<string, CancellationToken, Task<string?>> _readHead;
	private readonly Func<string, Task> _reportStatusAsync;

	/// <summary>Creates the remark tools.</summary>
	/// <param name="dispatcher">Resolves the caller's project.</param>
	/// <param name="books">The projects' remarks.</param>
	/// <param name="readHead">Reads a project root's HEAD commit, or null while it is unborn.</param>
	/// <param name="reportStatusAsync">Reports an answered remark in the status bar.</param>
	public RemarkTools(
		AgentControlDispatcher dispatcher,
		RemarkBooks books,
		Func<string, CancellationToken, Task<string?>> readHead,
		Func<string, Task> reportStatusAsync)
	{
		_dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
		_books = books ?? throw new ArgumentNullException(nameof(books));
		_readHead = readHead ?? throw new ArgumentNullException(nameof(readHead));
		_reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));
	}

	/// <summary>Builds <c>pact_reply_remark</c> and <c>pact_list_remarks</c>.</summary>
	public IReadOnlyList<AgentControlTool> Create() =>
	[
		new AgentControlTool(
			"pact_reply_remark",
			"Answer one of the user's remarks on working-tree or historical commit changes by its id (C1, C2, …). "
				+ "Pass resolve: true once the remark is dealt with.",
			() => AgentControlSchema.Object(
				new()
				{
					["id"] = AgentControlSchema.String("The remark id, such as C3."),
					["text"] = AgentControlSchema.String("Your answer."),
					["resolve"] = new JsonObject
					{
						["type"] = "boolean",
						["description"] = "Mark the remark resolved."
					}
				},
				"id",
				"text"),
			ReplyAsync),
		new AgentControlTool(
			"pact_list_remarks",
			"The user's remark threads on this project's working-tree and historical changes: id, file, side, line, optional targetCommit and originalPath, the "
				+ "line's text as the user saw it, the compared commit, whether the line has changed since "
				+ "(outdated), state and messages. Use it when you lost the prompt that carried them.",
			() => AgentControlSchema.Object(
				new()
				{
					["state"] = AgentControlSchema.Enumerated("Which threads; open (not resolved) when omitted.", States)
				}),
			ListAsync)
	];

	private async Task<AgentControlResultData> ReplyAsync(AgentControlToolCall call, CancellationToken cancellationToken)
	{
		if (!TryGetRequiredString(call.Arguments, "id", out var id, out var error)
			|| !TryGetRequiredString(call.Arguments, "text", out var text, out error)
			|| !TryGetOptionalBoolean(call.Arguments, "resolve", out var resolve, out error))
		{
			return error;
		}

		var (book, refusal) = await ResolveBookAsync(call, cancellationToken).ConfigureAwait(false);
		if (book is null)
		{
			return refusal!;
		}

		var reply = await book.AgentReplyAsync(id.Trim(), text, resolve == true, cancellationToken).ConfigureAwait(false);
		if (!reply.Found)
		{
			var open = reply.OpenIds.Count == 0
				? "There are no open remarks."
				: $"Open remarks: {string.Join(", ", reply.OpenIds)}.";
			return new AgentControlResultData($"unknown-remark: No remark {id.Trim()} in this project. {open}", IsError: true);
		}

		await _reportStatusAsync("Agent action: remark answered").ConfigureAwait(false);
		return new AgentControlResultData("Reply recorded.", IsError: false);
	}

	private async Task<AgentControlResultData> ListAsync(AgentControlToolCall call, CancellationToken cancellationToken)
	{
		if (!TryGetOptionalString(call.Arguments, "state", out var state, out var error))
		{
			return error;
		}

		state ??= "open";
		if (!States.Contains(state, StringComparer.Ordinal))
		{
			return new AgentControlResultData($"Argument 'state' must be one of: {string.Join(", ", States)}.", IsError: true);
		}

		var (book, refusal) = await ResolveBookAsync(call, cancellationToken).ConfigureAwait(false);
		if (book is null)
		{
			return refusal!;
		}

		var head = await _readHead(book.ProjectRoot, cancellationToken).ConfigureAwait(false);
		JsonArray items = [];
		foreach (var thread in book.Threads)
		{
			// The agent sees what the user sent, never what the user is still writing.
			var messages = thread.Messages.Where(message => message.Delivery != RemarkDelivery.Draft).ToArray();
			if (messages.Length == 0 || !Matches(thread.State, state))
			{
				continue;
			}

			var placement = await RemarkPlacer.PlaceOnDiskAsync(book, thread, head, cancellationToken).ConfigureAwait(false);
			JsonArray messageItems = [];
			foreach (var message in messages)
			{
				messageItems.Add(new JsonObject
				{
					["author"] = message.Author == RemarkAuthor.Agent ? "agent" : "user",
					["text"] = message.Text
				});
			}

			var item = new JsonObject
			{
				["id"] = thread.Id,
				["path"] = thread.Anchor.Path,
				["side"] = thread.Anchor.Side == RemarkSide.Old ? "old" : "new",
				["line"] = placement.Line ?? thread.Anchor.Line,
				["lineText"] = thread.Anchor.LineText,
				["baseCommit"] = thread.Anchor.BaseCommit,
				["outdated"] = placement.IsOutdated,
				["state"] = StateName(thread.State),
				["messages"] = messageItems
			};
			if (thread.Anchor.TargetCommit is { } target)
			{ item["targetCommit"] = target; }
			if (thread.Anchor.OriginalPath is { } original)
			{ item["originalPath"] = original; }
			items.Add(item);
		}

		return new AgentControlResultData(items.ToJsonString(new JsonSerializerOptions()), IsError: false);
	}

	private async Task<(RemarkBook? Book, AgentControlResultData? Refusal)> ResolveBookAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		var (root, refusal) = await _dispatcher.ResolveProjectRootAsync(call.SessionId ?? string.Empty, cancellationToken).ConfigureAwait(false);
		if (root is null)
		{
			var failure = refusal?.Failure;
			return (null, new AgentControlResultData(
				failure is null ? "The agent action was refused." : $"{failure.Code}: {failure.Message}",
				IsError: true));
		}

		var book = _books.For(root);
		await book.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
		return (book, null);
	}

	private static bool Matches(RemarkThreadState threadState, string filter) => filter switch
	{
		"all" => true,
		"open" => threadState != RemarkThreadState.Resolved,
		_ => string.Equals(StateName(threadState), filter, StringComparison.Ordinal)
	};

	private static string StateName(RemarkThreadState state) => state switch
	{
		RemarkThreadState.Draft => "draft",
		RemarkThreadState.Sent => "sent",
		RemarkThreadState.DeliveryUncertain => "uncertain",
		RemarkThreadState.Answered => "answered",
		_ => "resolved"
	};
}
using System.Text.Json.Nodes;
using Pact.Core.Boards;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>Project-only card tools, with bounded responses and explicit revision checks.</summary>
public static class BoardTools
{
	private static readonly string[] Names = ["pact_list_cards", "pact_get_card", "pact_create_card", "pact_update_card",
		"pact_add_card_comment", "pact_delete_card_comment", "pact_archive_card", "pact_take_next_card"];

	/// <summary>Identifies catalogue entries requiring a project owner.</summary>
	public static bool IsBoardTool(string name) => Names.Contains(name, StringComparer.Ordinal);

	/// <summary>Builds the eight tools without exposing project or actor identifiers as input.</summary>
	public static IReadOnlyList<AgentControlTool> Create(AgentControlDispatcher dispatcher)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		return Names.Select(name => new AgentControlTool(name, Description(name), () => Schema(name), async (call, cancellationToken) =>
		{
			var result = await dispatcher.ExecuteBoardToolAsync(call.SessionId ?? string.Empty, name, call.Arguments, cancellationToken);
			return new Pact.Infrastructure.AgentControl.AgentControlResultData(result.Payload
				?? $"{result.Failure?.Code}: {result.Failure?.Message}", !result.Succeeded);
		})).ToArray();
	}

	private static string Description(string name) => name switch
	{
		"pact_list_cards" => "List this project's active cards in manual order. Archive uses afterNumber pagination; active cards fit one response.",
		"pact_get_card" => "Read one card and a page of recent comments. Use beforeNumber for older comments.",
		"pact_create_card" => "Create a Backlog card. Put the closure condition in description. After a lost response, read the board before retrying creation.",
		"pact_update_card" => "Patch only supplied fields. Field updates require expectedCardRevision; explicit moves require expectedOrderRevision. A pure reorder needs only the order revision. Any status transition is allowed. move=true with no beforeCardId appends to the target column.",
		"pact_add_card_comment" => "Append a comment as the calling session. After a lost response, read comments before retrying.",
		"pact_delete_card_comment" => "Delete a comment explicitly, regardless of author, in this project.",
		"pact_archive_card" => "Archive a Done card, or restore it at the end of Done. Requires the displayed card revision.",
		_ => "Atomically take the first Ready card, move it to InProgress and assign yourself. Requires the displayed order revision. Empty Ready returns Empty; never analyses Backlog. Do not refresh the token merely to retry a lost response."
	};

	private static JsonObject Schema(string name)
	{
		JsonObject fields = [];
		List<string> required = [];
		void Add(string field, JsonObject schema, bool mandatory = false)
		{
			fields[field] = schema;
			if (mandatory)
			{
				required.Add(field);
			}
		}
		void Text(string field, bool mandatory = false)
		{
			Add(field, AgentControlSchema.String(field), mandatory);
		}

		void Boolean(string field)
		{
			Add(field, new() { ["type"] = "boolean" });
		}

		void Status()
		{
			Add("status", AgentControlSchema.Enumerated("Card status.", Enum.GetNames<BoardCardStatus>()));
		}

		void Cursor(string field)
		{
			Add(field, new() { ["type"] = "integer", ["minimum"] = 1 });
		}

		if (name is not ("pact_list_cards" or "pact_create_card" or "pact_take_next_card"))
		{
			Text("cardId", true);
		}
		switch (name)
		{
			case "pact_list_cards":
				Boolean("archived");
				Status();
				Cursor("afterNumber");
				Add("limit", AgentControlSchema.Integer("Archive page size.", 50, 1, 100));
				break;
			case "pact_get_card":
				Cursor("beforeNumber");
				Add("limit", AgentControlSchema.Integer("Comment page size.", 10, 1, 20));
				break;
			case "pact_create_card":
				Text("title", true);
				Text("description");
				Text("jiraUrl");
				break;
			case "pact_update_card":
				Text("expectedCardRevision");
				Text("expectedOrderRevision");
				Text("title");
				Text("description");
				Text("jiraUrl");
				Text("assignedSessionId");
				Text("beforeCardId");
				Boolean("move");
				Status();
				foreach (var field in new[] { "jiraUrl", "assignedSessionId", "beforeCardId" })
				{
					fields[field]!["type"] = new JsonArray("string", "null");
				}
				break;
			case "pact_add_card_comment":
				Text("text", true);
				break;
			case "pact_delete_card_comment":
				Text("commentId", true);
				break;
			case "pact_archive_card":
				Text("expectedCardRevision", true);
				Boolean("archived");
				required.Add("archived");
				break;
			case "pact_take_next_card":
				Text("expectedOrderRevision", true);
				break;
		}
		var schema = AgentControlSchema.Object(fields, required.ToArray());
		schema["additionalProperties"] = false;
		return schema;
	}

	internal static BoardToolRequest Parse(string name, JsonNode arguments)
	{
		if (!IsBoardTool(name) || arguments is not JsonObject args)
		{
			throw new ArgumentException("Unknown tool or invalid arguments.");
		}
		var schema = Schema(name);
		var properties = (JsonObject)schema["properties"]!;
		foreach (var pair in args)
		{
			if (!properties.ContainsKey(pair.Key))
			{
				throw new ArgumentException($"Unknown argument: {pair.Key}.");
			}
		}
		string? Text(string key, bool required = false)
		{
			if (args[key] is null)
			{
				if (required || (args.ContainsKey(key) && key is not ("jiraUrl" or "assignedSessionId" or "beforeCardId")))
				{
					throw new ArgumentException($"{key} must be a string.");
				}
				return null;
			}
			return args[key]!.GetValue<string>();
		}
		bool Boolean(string key, bool fallback = false)
		{
			return args.ContainsKey(key)
			? args[key]?.GetValue<bool>() ?? throw new ArgumentException($"{key} must be boolean.") : fallback;
		}

		long? Number(string key)
		{
			if (!args.ContainsKey(key))
			{
				return null;
			}
			if (args[key] is JsonValue node)
			{
				if (node.TryGetValue<long>(out var wide) && wide > 0)
				{
					return wide;
				}
				if (node.TryGetValue<int>(out var narrow) && narrow > 0)
				{
					return narrow;
				}
			}
			throw new ArgumentException($"{key} must be a positive integer.");
		}
		BoardCardStatus? Status()
		{
			var value = Text("status");
			if (value is null)
			{
				return null;
			}
			return Enum.GetNames<BoardCardStatus>().Contains(value, StringComparer.Ordinal)
				? Enum.Parse<BoardCardStatus>(value) : throw new ArgumentException("Unknown status.");
		}
		var cardId = Text("cardId", name is not ("pact_list_cards" or "pact_create_card" or "pact_take_next_card"));
		var changesFields = new[] { "title", "description", "status", "jiraUrl", "assignedSessionId" }.Any(args.ContainsKey);
		if (name == "pact_update_card" && Boolean("move") && string.IsNullOrWhiteSpace(Text("expectedOrderRevision")))
		{
			throw new ArgumentException("move requires expectedOrderRevision.");
		}
		BoardCommand? command = name switch
		{
			"pact_create_card" => new CreateCard(Text("title", true)!, Text("description") ?? string.Empty, Text("jiraUrl")),
			"pact_update_card" => new UpdateCard(cardId!, Text("expectedCardRevision", !Boolean("move") || changesFields) ?? string.Empty, new()
			{
				Title = args.ContainsKey("title") ? new(Text("title", true)!) : null,
				Description = args.ContainsKey("description") ? new(Text("description", true)!) : null,
				JiraUrl = args.ContainsKey("jiraUrl") ? new(Text("jiraUrl")) : null,
				AssignedSessionId = args.ContainsKey("assignedSessionId") ? new(Text("assignedSessionId")) : null,
				Status = Status() is { } status ? new(status) : null
			}, Text("beforeCardId"), Text("expectedOrderRevision"), Boolean("move")),
			"pact_add_card_comment" => new AddComment(cardId!, Text("text", true)!),
			"pact_delete_card_comment" => new DeleteComment(cardId!, Text("commentId", true)!),
			"pact_archive_card" => new SetArchived(cardId!, args.ContainsKey("archived") ? Boolean("archived") : throw new ArgumentException("archived is required."), Text("expectedCardRevision", true)!),
			"pact_take_next_card" => new TakeNext(Text("expectedOrderRevision", true)!),
			_ => null
		};
		var limit = Number("limit") ?? (name == "pact_get_card" ? 10 : 50);
		if (limit > (name == "pact_get_card" ? 20 : 100))
		{
			throw new ArgumentException("Page limit is too large.");
		}
		var archived = Boolean("archived");
		var after = Number("afterNumber");
		if (name == "pact_list_cards" && !archived && (after is not null || args.ContainsKey("limit")))
		{
			throw new ArgumentException("Only the archive is paginated.");
		}
		return new(command, cardId, archived, Status(), (int)limit, after, Number("beforeNumber"));
	}
}

internal sealed record BoardToolRequest(BoardCommand? Command, string? CardId, bool Archived, BoardCardStatus? Status,
	int Limit, long? AfterNumber, long? BeforeNumber);

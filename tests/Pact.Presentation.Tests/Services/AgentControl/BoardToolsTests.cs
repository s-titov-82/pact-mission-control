using System.Text;
using System.Text.Json.Nodes;
using Pact.Core.Boards;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class BoardToolsTests
{
	private readonly AgentControlDispatcherTests.FakeAgentControlHost _host = new();
	private AgentControlToolSet Tools => new(BoardTools.Create(new(_host)));
	private async Task<JsonNode> Call(string name, JsonObject args, string session = "session-project")
	{
		var result = await Tools.InvokeAsync(new(session, false, name, args), CancellationToken.None);
		result.IsError.ShouldBeFalse(result.Text);
		return JsonNode.Parse(result.Text)!;
	}

	[Test]
	public async Task All_card_commands_persist_expected_effects()
	{
		var created = await Call("pact_create_card", new() { ["title"] = "Task", ["description"] = "Closure condition" });
		var id = created["cardId"]!.GetValue<string>();
		var listed = await Call("pact_list_cards", new());
		listed["cards"]!.AsArray().Count.ShouldBe(1);
		await Call("pact_update_card", new() { ["cardId"] = id, ["expectedCardRevision"] = created["card"]!["cardRevision"]!.GetValue<string>(), ["status"] = "Ready" });
		var order = (await Call("pact_list_cards", new()))["orderRevision"]!.GetValue<string>();
		var taken = await Call("pact_take_next_card", new() { ["expectedOrderRevision"] = order });
		taken["outcome"]!.GetValue<string>().ShouldBe("Applied");
		_host.Board.Cards.Single().AssignedSessionId.ShouldBe("session-project");
		var commented = await Call("pact_add_card_comment", new() { ["cardId"] = id, ["text"] = "Result" });
		var read = await Call("pact_get_card", new() { ["cardId"] = id });
		read["description"]!.GetValue<string>().ShouldBe("Closure condition");
		read["comments"]!.AsArray().Count.ShouldBe(1);
		await Call("pact_delete_card_comment", new() { ["cardId"] = id, ["commentId"] = commented["commentId"]!.GetValue<string>() });
		_host.Board.Cards.Single().Comments.ShouldBeEmpty();
		var done = await Call("pact_update_card", new() { ["cardId"] = id, ["expectedCardRevision"] = read["card"]!["cardRevision"]!.GetValue<string>(), ["status"] = "Done" });
		await Call("pact_archive_card", new() { ["cardId"] = id, ["expectedCardRevision"] = done["card"]!["cardRevision"]!.GetValue<string>(), ["archived"] = true });
		(await Call("pact_list_cards", new()))["cards"]!.AsArray().ShouldBeEmpty();
		var archived = await Call("pact_list_cards", new() { ["archived"] = true });
		await Call("pact_archive_card", new() { ["cardId"] = id, ["expectedCardRevision"] = archived["cards"]![0]!["cardRevision"]!.GetValue<string>(), ["archived"] = false });
		_host.Board.Cards.Single().IsArchived.ShouldBeFalse();
	}

	[Test]
	public async Task Lost_take_response_cannot_claim_another_card_with_same_token()
	{
		_host.Board = new() { Cards = [Card(1, BoardCardStatus.Ready), Card(2, BoardCardStatus.Ready)], NextCardNumber = 3 };
		var revision = BoardRevisions.ForOrder(_host.Board);
		await Call("pact_take_next_card", new() { ["expectedOrderRevision"] = revision });
		var retry = await Call("pact_take_next_card", new() { ["expectedOrderRevision"] = revision });
		retry["outcome"]!.GetValue<string>().ShouldBe("Conflict");
		_host.Board.Cards.Count(card => card.Status == BoardCardStatus.InProgress).ShouldBe(1);
	}

	[Test]
	public async Task Empty_ready_does_not_touch_backlog()
	{
		_host.Board = new() { Cards = [Card(1)], NextCardNumber = 2 };
		var result = await Call("pact_take_next_card", new() { ["expectedOrderRevision"] = BoardRevisions.ForOrder(_host.Board) });
		result["outcome"]!.GetValue<string>().ShouldBe("Empty");
		_host.Board.Cards.Single().Status.ShouldBe(BoardCardStatus.Backlog);
	}

	[TestCase("session-root")]
	[TestCase("ghost")]
	[TestCase("stale")]
	public async Task Non_project_or_stale_callers_are_refused(string session)
	{
		var result = await Tools.InvokeAsync(new(session, false, "pact_list_cards", new JsonObject()), CancellationToken.None);
		result.IsError.ShouldBeTrue();
		Tools.BuildToolsList(includeBoard: false)["tools"]!.AsArray().ShouldBeEmpty();
	}

	[Test]
	public async Task Foreign_assignment_and_unknown_arguments_are_refused()
	{
		_host.Board = new() { Cards = [Card(1)], NextCardNumber = 2 };
		var args = new JsonObject { ["cardId"] = "1", ["expectedCardRevision"] = BoardRevisions.ForCard(_host.Board.Cards.Single()), ["assignedSessionId"] = "stale" };
		(await Tools.InvokeAsync(new("session-project", false, "pact_update_card", args), CancellationToken.None)).IsError.ShouldBeTrue();
		_host.Board.Cards.Single().AssignedSessionId.ShouldBeNull();
		(await Tools.InvokeAsync(new("session-project", false, "pact_list_cards", new JsonObject { ["projectId"] = "foreign" }), CancellationToken.None)).IsError.ShouldBeTrue();
	}

	[Test]
	public async Task Deleted_comment_boundary_and_new_comments_do_not_restart_history()
	{
		_host.Board = new() { Cards = [Card(1) with { Comments = Enumerable.Range(1, 5).Select(n => new BoardComment(n.ToString(), n, null, "You", DateTimeOffset.UnixEpoch, n.ToString())).ToArray(), NextCommentNumber = 6 }], NextCardNumber = 2 };
		var first = await Call("pact_get_card", new() { ["cardId"] = "1", ["limit"] = 2 });
		var cursor = first["nextBeforeNumber"]!.GetValue<long>();
		await Call("pact_delete_card_comment", new() { ["cardId"] = "1", ["commentId"] = cursor.ToString() });
		await Call("pact_add_card_comment", new() { ["cardId"] = "1", ["text"] = "New" });
		var second = await Call("pact_get_card", new() { ["cardId"] = "1", ["limit"] = 2, ["beforeNumber"] = cursor });
		second["comments"]!.AsArray().Select(comment => comment!["number"]!.GetValue<long>()).ShouldBe([2L, 3L]);
	}

	[Test]
	public async Task Pure_move_needs_only_order_token_and_missing_order_is_invalid()
	{
		_host.Board = new() { Cards = [Card(1), Card(2)], NextCardNumber = 3 };
		var result = await Call("pact_update_card", new() { ["cardId"] = "2", ["move"] = true, ["beforeCardId"] = "1", ["expectedOrderRevision"] = BoardRevisions.ForOrder(_host.Board) });
		result["outcome"]!.GetValue<string>().ShouldBe("Applied");
		_host.Board.Cards.Select(card => card.Number).ShouldBe([2L, 1L]);
		var missing = await Tools.InvokeAsync(new("session-project", false, "pact_update_card", new JsonObject { ["cardId"] = "2", ["move"] = true }), CancellationToken.None);
		missing.IsError.ShouldBeTrue();
		missing.Text.ShouldContain("invalid-argument");
	}

	[Test]
	public async Task Restoring_archive_boundary_does_not_break_next_page()
	{
		_host.Board = new() { Cards = Enumerable.Range(1, 5).Select(n => Card(n, BoardCardStatus.Done) with { IsArchived = true }).ToArray(), NextCardNumber = 6 };
		var first = await Call("pact_list_cards", new() { ["archived"] = true, ["limit"] = 2 });
		var cursor = first["nextAfterNumber"]!.GetValue<long>();
		var boundary = _host.Board.Cards.Single(card => card.Number == cursor);
		await Call("pact_archive_card", new() { ["cardId"] = boundary.Id, ["expectedCardRevision"] = BoardRevisions.ForCard(boundary), ["archived"] = false });
		var next = await Call("pact_list_cards", new() { ["archived"] = true, ["limit"] = 2, ["afterNumber"] = cursor });
		next["cards"]!.AsArray().Select(card => card!["number"]!.GetValue<long>()).ShouldBe([3L, 4L]);
	}

	[Test]
	public async Task Escaped_unicode_list_and_detail_fit_one_mebibyte()
	{
		_host.Board = new() { Cards = Enumerable.Range(1, BoardLimits.ActiveCards).Select(n => Card(n) with { Title = new string('\u0436', BoardLimits.Title) }).ToArray(), NextCardNumber = 201 };
		var list = await Tools.InvokeAsync(new("session-project", false, "pact_list_cards", new JsonObject()), CancellationToken.None);
		Encoding.UTF8.GetByteCount(list.Text).ShouldBeLessThan(1024 * 1024);
		_host.Board = new() { Cards = [Card(1) with { Description = new string('\u0436', BoardLimits.Description), Comments = Enumerable.Range(1, 20).Select(n => new BoardComment(n.ToString(), n, null, new string('\u0436', 128), DateTimeOffset.UnixEpoch, new string('\u0001', BoardLimits.Comment))).ToArray() }] };
		var detail = await Tools.InvokeAsync(new("session-project", false, "pact_get_card", new JsonObject { ["cardId"] = "1", ["limit"] = 20 }), CancellationToken.None);
		detail.IsError.ShouldBeFalse(detail.Text);
		Encoding.UTF8.GetByteCount(detail.Text).ShouldBeLessThan(1024 * 1024);
	}

	private static BoardCard Card(int number, BoardCardStatus status = BoardCardStatus.Backlog) => new()
	{
		Id = number.ToString(),
		Number = number,
		Title = "Task",
		Status = status,
		CreatedAt = DateTimeOffset.UnixEpoch,
		UpdatedAt = DateTimeOffset.UnixEpoch
	};
}

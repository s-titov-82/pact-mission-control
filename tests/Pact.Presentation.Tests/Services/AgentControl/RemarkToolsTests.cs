using System.Text.Json.Nodes;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.Tests.Fakes;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class RemarkToolsTests : IDisposable
{
	[Test]
	public async Task Local_old_mcp_uses_captured_base_and_original_path_after_head_changes()
	{
		await Book.AddAsync(new("src/X.cs", RemarkSide.Old, 2, "gone", [], [], "captured", null, OriginalPath: "src/Old.cs"), null, "review", CancellationToken.None);
		await Book.CompleteSendAsync((await Book.BeginSendAsync("s1", CancellationToken.None))!, Confirmed, CancellationToken.None);
		CapturedCommitReader reader = new("captured", "src/Old.cs", "one\ngone\n");
		var result = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject(), reader);
		var item = JsonNode.Parse(result.Text)!.AsArray().ShouldHaveSingleItem()!;
		((bool?)item["outdated"]).ShouldBe(false);
		((int?)item["line"]).ShouldBe(2);
		((string?)item["baseCommit"]).ShouldBe("captured");
		reader.Available = false;
		var missing = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject(), reader);
		((bool?)JsonNode.Parse(missing.Text)!.AsArray()[0]!["outdated"]).ShouldBe(true);
	}
	[TestCase(RemarkSide.New)]
	[TestCase(RemarkSide.Old)]
	public async Task Historical_mcp_preserves_sha_line_and_not_outdated_when_head_and_disk_differ(RemarkSide side)
	{
		var target = new string('a', 40);
		var parent = new string('b', 40);
		RemarkAnchor anchor = new("src/X.cs", side, 2, "b", ["a"], [], parent, null, target, "src/Old.cs");
		await Book.AddAsync(anchor, side == RemarkSide.New ? "a\nb\n" : null, "historical", CancellationToken.None);
		var batch = await Book.BeginSendAsync("s1", CancellationToken.None);
		await Book.CompleteSendAsync(batch!, Confirmed, CancellationToken.None);
		await WriteFileAsync();
		await File.WriteAllTextAsync(Path.Combine(_root.Path, "src", "X.cs"), "x\na\nb\n");
		var result = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject());
		var item = JsonNode.Parse(result.Text)!.AsArray().ShouldHaveSingleItem()!;
		((string?)item["targetCommit"]).ShouldBe(target);
		((string?)item["baseCommit"]).ShouldBe(parent);
		((string?)item["originalPath"]).ShouldBe("src/Old.cs");
		((int?)item["line"]).ShouldBe(2);
		((bool?)item["outdated"]).ShouldBe(false);
	}
	private static readonly PromptDeliveryResult Confirmed = new(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true);

	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();
	private readonly RemarkBooks _books = new(TimeProvider.System);
	private readonly List<string> _statuses = [];

	public void Dispose()
	{
		_books.Dispose();
		_root.Dispose();
	}

	[Test]
	public async Task A_reply_answers_the_thread()
	{
		await AddSentAsync();

		var result = await InvokeAsync("session-project", "pact_reply_remark", new JsonObject { ["id"] = "C1", ["text"] = "because" });

		result.IsError.ShouldBeFalse(result.Text);
		result.Text.ShouldBe("Reply recorded.");
		Book.Threads.Single().State.ShouldBe(RemarkThreadState.Answered);
		_statuses.ShouldBe(["Agent action: remark answered"]);
	}

	[Test]
	public async Task A_reply_with_resolve_resolves_it()
	{
		await AddSentAsync();

		await InvokeAsync("session-project", "pact_reply_remark", new JsonObject { ["id"] = "c1", ["text"] = "done", ["resolve"] = true });

		Book.Threads.Single().State.ShouldBe(RemarkThreadState.Resolved);
	}

	[Test]
	public async Task An_unknown_id_is_refused_with_the_open_ids()
	{
		var none = await InvokeAsync("session-project", "pact_reply_remark", new JsonObject { ["id"] = "C9", ["text"] = "x" });
		await AddSentAsync();

		var some = await InvokeAsync("session-project", "pact_reply_remark", new JsonObject { ["id"] = "C9", ["text"] = "x" });

		none.IsError.ShouldBeTrue();
		none.Text.ShouldBe("unknown-remark: No remark C9 in this project. There are no open remarks.");
		some.Text.ShouldBe("unknown-remark: No remark C9 in this project. Open remarks: C1.");
	}

	[Test]
	public async Task The_list_leaves_out_drafts_and_marks_outdated_threads()
	{
		await AddSentAsync();
		await Book.SaveDraftAsync("C1", "unsent follow-up", CancellationToken.None);
		await Book.AddAsync(NewAnchor(), "a\nb\n", "only a draft", CancellationToken.None);
		await Book.AddAsync(new RemarkAnchor("src/X.cs", RemarkSide.Old, 9, "gone();", [], [], "old-head", null), null, "old", CancellationToken.None);
		var batch = await Book.BeginSendAsync("s1", CancellationToken.None);
		await Book.CompleteSendAsync(batch!, Confirmed, CancellationToken.None);
		await Book.SaveDraftAsync("C2", "still a draft", CancellationToken.None);

		var result = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject());

		result.IsError.ShouldBeFalse(result.Text);
		var list = JsonNode.Parse(result.Text)!.AsArray();
		list.Select(item => (string?)item!["id"]).ShouldBe(["C1", "C2", "C3"]);
		var first = list[0]!;
		((string?)first["path"]).ShouldBe("src/X.cs");
		((string?)first["side"]).ShouldBe("new");
		((int?)first["line"]).ShouldBe(2);
		((string?)first["lineText"]).ShouldBe("b");
		((string?)first["baseCommit"]).ShouldBe("abc");
		((bool?)first["outdated"]).ShouldBe(false);
		((string?)first["state"]).ShouldBe("sent");
		first["messages"]!.AsArray().Select(message => ((string?)message!["author"], (string?)message["text"]))
			.ShouldBe([("user", "why?"), ("user", "unsent follow-up")]);
		list[1]!["messages"]!.AsArray().Select(message => (string?)message!["text"]).ShouldBe(["only a draft"]);
		((bool?)list[2]!["outdated"]).ShouldBe(true);
	}

	[Test]
	public async Task A_thread_with_only_a_draft_is_not_listed()
	{
		await Book.AddAsync(NewAnchor(), "a\nb\n", "not yet", CancellationToken.None);
		await WriteFileAsync();

		var result = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject { ["state"] = "all" });

		JsonNode.Parse(result.Text)!.AsArray().ShouldBeEmpty();
	}

	[Test]
	public async Task The_state_filter_picks_threads()
	{
		await AddSentAsync();
		await AddSentAsync();
		await Book.ResolveAsync("C2", CancellationToken.None);

		var open = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject());
		var resolved = await InvokeAsync("session-project", "pact_list_remarks", new JsonObject { ["state"] = "resolved" });

		JsonNode.Parse(open.Text)!.AsArray().Select(item => (string?)item!["id"]).ShouldBe(["C1"]);
		JsonNode.Parse(resolved.Text)!.AsArray().Select(item => (string?)item!["id"]).ShouldBe(["C2"]);
	}

	[Test]
	public async Task A_root_session_is_refused()
	{
		var result = await InvokeAsync("session-root", "pact_list_remarks", new JsonObject());

		result.IsError.ShouldBeTrue();
		result.Text.ShouldStartWith("owner-not-a-project:");
	}

	private RemarkBook Book => _books.For(_root.Path);

	private async Task AddSentAsync()
	{
		await WriteFileAsync();
		await Book.AddAsync(NewAnchor(), "a\nb\n", "why?", CancellationToken.None);
		var batch = await Book.BeginSendAsync("s1", CancellationToken.None);
		await Book.CompleteSendAsync(batch!, Confirmed, CancellationToken.None);
	}

	private async Task WriteFileAsync()
	{
		Directory.CreateDirectory(Path.Combine(_root.Path, "src"));
		await File.WriteAllTextAsync(Path.Combine(_root.Path, "src", "X.cs"), "a\nb\n");
	}

	private Task<AgentControlResultData> InvokeAsync(string sessionId, string tool, JsonObject arguments, IGitCommitReader? commitReader = null)
	{
		AgentControlDispatcherTests.FakeAgentControlHost host = new() { ProjectRoot = _root.Path };
		RemarkTools remarks = new(
			new AgentControlDispatcher(host),
			_books,
			commitReader ?? new GitWorkingTreeReader(new GitCliRunner()),
			status =>
			{
				_statuses.Add(status);
				return Task.CompletedTask;
			});
		return new AgentControlToolSet(remarks.Create())
			.InvokeAsync(new AgentControlToolCall(sessionId, IsOrchestrator: false, tool, arguments), CancellationToken.None);
	}

	private static RemarkAnchor NewAnchor() => new("src/X.cs", RemarkSide.New, 2, "b", ["a"], [], "abc", null);
}

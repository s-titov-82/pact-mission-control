using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Presentation.Services.Remarks;

namespace Pact.Presentation.Tests.Services.Remarks;

public sealed class RemarkBookTests : IDisposable
{
	private static readonly PromptDeliveryResult Confirmed = new(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true);
	private static readonly PromptDeliveryResult Unconfirmed = new(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true, SubmitAttempted: true);
	private static readonly PromptDeliveryResult NothingWritten = new(PromptDeliveryOutcome.NotRunning, "Not running");
	private static readonly PromptDeliveryResult WrittenNotSubmitted = new(PromptDeliveryOutcome.PasteNotShown, WriteAttempted: true);

	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();

	public void Dispose() => _root.Dispose();

	[Test]
	public async Task Clearing_a_resolved_review_removes_all_project_sources_and_never_reuses_ids()
	{
		using var book = Book();
		await AddAsync(book);
		await book.AddAsync(NewAnchor() with { TargetCommit = new string('a', 40) }, "a\nb\n", "historical", CancellationToken.None);
		await book.CompleteSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, Confirmed, CancellationToken.None);
		await book.ResolveAsync("C1", CancellationToken.None);
		await book.ResolveAsync("C2", CancellationToken.None);
		var expected = book.Threads.ToArray();
		(await book.TryClearReviewAsync(expected, CancellationToken.None)).ShouldBeTrue();
		book.Threads.ShouldBeEmpty();
		(await AddAsync(book)).Id.ShouldBe("C3");
		using var restored = Book();
		await restored.EnsureLoadedAsync(CancellationToken.None);
		restored.Threads.Select(thread => thread.Id).ShouldBe(["C3"]);
	}

	[TestCase("add")]
	[TestCase("reopen")]
	[TestCase("reply")]
	[TestCase("closed_commit")]
	public async Task Cleanup_rejects_a_changed_review_without_removing_any_threads(string change)
	{
		using var book = Book();
		await AddAsync(book);
		await book.CompleteSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, Confirmed, CancellationToken.None);
		await book.ResolveAsync("C1", CancellationToken.None);
		var expected = book.Threads.ToArray();
		switch (change)
		{
			case "add":
				await AddAsync(book, "new question");
				break;
			case "reopen":
				await book.ReopenAsync("C1", CancellationToken.None);
				break;
			case "reply":
				await book.AgentReplyAsync("C1", "late answer", resolve: true, CancellationToken.None);
				break;
			case "closed_commit":
				await book.AddAsync(NewAnchor() with { TargetCommit = new string('b', 40) }, "a\nb\n", "new historical question", CancellationToken.None);
				await book.CompleteSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, Confirmed, CancellationToken.None);
				await book.ResolveAsync("C2", CancellationToken.None);
				break;
		}
		var before = book.Threads.ToArray();
		(await book.TryClearReviewAsync(expected, CancellationToken.None)).ShouldBeFalse();
		book.Threads.ShouldBe(before);
	}

	[TestCase(false, RemarkDelivery.Sent)]
	[TestCase(true, RemarkDelivery.Draft)]
	[TestCase(true, RemarkDelivery.Sending)]
	[TestCase(true, RemarkDelivery.Uncertain)]
	public async Task Cleanup_never_removes_open_or_pending_threads(bool resolved, RemarkDelivery delivery)
	{
		var thread = new RemarkThread("C1", NewAnchor(), [new("m1", RemarkAuthor.User, "text", DateTimeOffset.MinValue, delivery, "s1")], resolved);
		RemarkFileStore store = new(_root.Path);
		await store.SaveAsync(new(2, [thread]), CancellationToken.None);
		using var book = Book();
		await book.EnsureLoadedAsync(CancellationToken.None);
		(await book.TryClearReviewAsync(book.Threads.ToArray(), CancellationToken.None)).ShouldBeFalse();
		book.Threads.ShouldHaveSingleItem();
	}

	[Test]
	public async Task Sending_includes_open_drafts_from_all_project_sources()
	{
		using var book = Book();
		var working = await AddAsync(book, "working draft");
		var target = new string('a', 40);
		var historical = await book.AddAsync(NewAnchor() with { TargetCommit = target }, "a\nb\n", "historical draft", CancellationToken.None);
		var batch = (await book.BeginSendAsync("s1", CancellationToken.None)).ShouldNotBeNull();
		batch.Items.Select(item => item.Thread.Id).ShouldBe([working.Id, historical.Id]);
		await book.CompleteSendAsync(batch, Confirmed, CancellationToken.None);
		(await book.BeginSendAsync("s1", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task Ids_count_up_and_are_not_reused_after_a_discard()
	{
		using var book = Book();
		(await AddAsync(book)).Id.ShouldBe("C1");
		(await AddAsync(book)).Id.ShouldBe("C2");

		await book.SaveDraftAsync("C2", "  ", CancellationToken.None);

		book.Threads.Select(thread => thread.Id).ShouldBe(["C1"]);
		(await AddAsync(book)).Id.ShouldBe("C3");
		using var other = Book();
		(await AddAsync(other)).Id.ShouldBe("C4");
	}

	[Test]
	public async Task A_new_side_remark_keeps_a_snapshot_of_the_file()
	{
		using var book = Book();

		var thread = await book.AddAsync(NewAnchor(), "a\r\nb\n", "why?", CancellationToken.None);

		thread.Anchor.SnapshotId.ShouldNotBeNull();
		(await book.LoadSnapshotLinesAsync(thread.Anchor.SnapshotId, CancellationToken.None)).ShouldBe(["a", "b"]);
		thread.EditableDraft!.Text.ShouldBe("why?");
	}

	[Test]
	public async Task Begin_send_takes_every_draft_and_only_drafts()
	{
		using var book = Book();
		await AddAsync(book, "first");
		await AddAsync(book, "second");
		await book.CompleteSendAsync((await book.BeginSendAsync("s0", CancellationToken.None))!, Confirmed, CancellationToken.None);
		await book.SaveDraftAsync("C1", "more", CancellationToken.None);
		await AddAsync(book, "resolved one");
		await book.ResolveAsync("C3", CancellationToken.None);

		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		batch!.SessionId.ShouldBe("s1");
		var item = batch.Items.ShouldHaveSingleItem();
		item.Thread.Id.ShouldBe("C1");
		item.Messages.Select(message => message.Text).ShouldBe(["more"]);
		var sending = Thread(book, "C1").Messages[^1];
		sending.Delivery.ShouldBe(RemarkDelivery.Sending);
		sending.SessionId.ShouldBe("s1");
		(await book.BeginSendAsync("s1", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task A_late_delivery_result_does_not_undo_an_answer()
	{
		using var book = Book();
		await AddAsync(book);
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		await book.AgentReplyAsync("C1", "because", resolve: false, CancellationToken.None);
		await book.CompleteSendAsync(batch!, Unconfirmed, CancellationToken.None);

		Thread(book, "C1").State.ShouldBe(RemarkThreadState.Answered);
		Thread(book, "C1").Messages[0].Delivery.ShouldBe(RemarkDelivery.Sent);
	}

	[Test]
	public async Task A_late_delivery_result_does_not_undo_a_resolve()
	{
		using var book = Book();
		await AddAsync(book);
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		await book.AgentReplyAsync("C1", "done", resolve: true, CancellationToken.None);
		await book.CompleteSendAsync(batch!, Unconfirmed, CancellationToken.None);

		Thread(book, "C1").State.ShouldBe(RemarkThreadState.Resolved);
	}

	[Test]
	public async Task A_reply_drafted_during_delivery_stays_a_draft()
	{
		using var book = Book();
		await AddAsync(book, "first");
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		await book.SaveDraftAsync("C1", "and also", CancellationToken.None);
		await book.CompleteSendAsync(batch!, Confirmed, CancellationToken.None);

		var thread = Thread(book, "C1");
		thread.Messages.Select(message => (message.Text, message.Delivery))
			.ShouldBe([("first", RemarkDelivery.Sent), ("and also", RemarkDelivery.Draft)]);
		thread.LastSessionId.ShouldBe("s1");
	}

	[Test]
	public async Task Nothing_written_puts_the_messages_back_to_draft()
	{
		using var book = Book();
		await AddAsync(book);
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		await book.CompleteSendAsync(batch!, NothingWritten, CancellationToken.None);

		var message = Thread(book, "C1").Messages.ShouldHaveSingleItem();
		message.Delivery.ShouldBe(RemarkDelivery.Draft);
		message.SessionId.ShouldBeNull();
	}

	[Test]
	public async Task Nothing_written_keeps_a_newer_draft_separate()
	{
		using var book = Book();
		await AddAsync(book, "A");
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);
		await book.SaveDraftAsync("C1", "B", CancellationToken.None);

		await book.CompleteSendAsync(batch!, NothingWritten, CancellationToken.None);

		var thread = Thread(book, "C1");
		thread.Unsent.Select(message => message.Text).ShouldBe(["A", "B"]);
		thread.EditableDraft!.Text.ShouldBe("B");
		var next = await book.BeginSendAsync("s2", CancellationToken.None);
		next!.Items.ShouldHaveSingleItem().Messages.Select(message => message.Text).ShouldBe(["A", "B"]);
	}

	[Test]
	public async Task Written_but_not_submitted_is_delivery_uncertain_and_can_be_retried_or_marked_sent()
	{
		using var book = Book();
		await AddAsync(book);
		await AddAsync(book);
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);

		await book.CompleteSendAsync(batch!, WrittenNotSubmitted, CancellationToken.None);
		Thread(book, "C1").State.ShouldBe(RemarkThreadState.DeliveryUncertain);
		(await book.BeginSendAsync("s1", CancellationToken.None)).ShouldBeNull();

		await book.RetryAsync("C1", CancellationToken.None);
		await book.MarkSentAsync("C2", CancellationToken.None);

		Thread(book, "C1").State.ShouldBe(RemarkThreadState.Draft);
		Thread(book, "C2").State.ShouldBe(RemarkThreadState.Sent);
	}

	[Test]
	public async Task Retrying_an_uncertain_message_keeps_a_newer_draft()
	{
		using var book = Book();
		await AddAsync(book, "A");
		var batch = await book.BeginSendAsync("s1", CancellationToken.None);
		await book.CompleteSendAsync(batch!, WrittenNotSubmitted, CancellationToken.None);
		await book.SaveDraftAsync("C1", "B", CancellationToken.None);

		await book.RetryAsync("C1", CancellationToken.None);

		var thread = Thread(book, "C1");
		thread.Unsent.Select(message => message.Text).ShouldBe(["A", "B"]);
		thread.EditableDraft!.Text.ShouldBe("B");
	}

	[Test]
	public async Task A_sending_message_left_by_a_stopped_pact_loads_as_uncertain()
	{
		using var book = Book();
		await AddAsync(book);
		await book.BeginSendAsync("s1", CancellationToken.None);

		using var reopened = Book();
		await reopened.EnsureLoadedAsync(CancellationToken.None);

		Thread(reopened, "C1").State.ShouldBe(RemarkThreadState.DeliveryUncertain);
		var stored = await new RemarkFileStore(_root.Path).LoadAsync(CancellationToken.None);
		stored.Threads.Single().Messages.Single().Delivery.ShouldBe(RemarkDelivery.Uncertain);
	}

	[Test]
	public async Task An_abandoned_send_is_draft_before_writing_and_uncertain_after()
	{
		using var book = Book();
		await AddAsync(book);
		await book.AbandonSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, mayHaveWritten: false, CancellationToken.None);
		Thread(book, "C1").State.ShouldBe(RemarkThreadState.Draft);

		await book.AbandonSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, mayHaveWritten: true, CancellationToken.None);

		Thread(book, "C1").State.ShouldBe(RemarkThreadState.DeliveryUncertain);
	}

	[Test]
	public async Task An_unknown_id_lists_the_open_ones()
	{
		using var book = Book();
		await AddAsync(book);
		await AddAsync(book);
		await book.ResolveAsync("C2", CancellationToken.None);

		var reply = await book.AgentReplyAsync("C9", "hm", resolve: false, CancellationToken.None);

		reply.Found.ShouldBeFalse();
		reply.OpenIds.ShouldBe(["C1"]);
	}

	[Test]
	public async Task An_answer_without_resolve_reopens_a_resolved_thread()
	{
		using var book = Book();
		await AddAsync(book);
		await book.CompleteSendAsync((await book.BeginSendAsync("s1", CancellationToken.None))!, Confirmed, CancellationToken.None);
		await book.ResolveAsync("C1", CancellationToken.None);

		(await book.AgentReplyAsync("C1", "actually", resolve: false, CancellationToken.None)).Found.ShouldBeTrue();

		Thread(book, "C1").State.ShouldBe(RemarkThreadState.Answered);
	}

	[Test]
	public async Task Changes_are_saved_before_changed_is_raised()
	{
		using var book = Book();
		await book.EnsureLoadedAsync(CancellationToken.None);
		List<int> seen = [];
		book.Changed += (_, _) =>
			seen.Add(new RemarkFileStore(_root.Path).LoadAsync(CancellationToken.None).GetAwaiter().GetResult().Threads.Count);

		await AddAsync(book);

		seen.ShouldBe([1]);
	}

	private RemarkBook Book() => new(_root.Path, new RemarkFileStore(_root.Path), TimeProvider.System);

	private static Task<RemarkThread> AddAsync(RemarkBook book, string text = "why?") =>
		book.AddAsync(NewAnchor(), "a\nb\n", text, CancellationToken.None);

	private static RemarkThread Thread(RemarkBook book, string id) => book.Threads.Single(thread => thread.Id == id);

	private static RemarkAnchor NewAnchor() => new("src/X.cs", RemarkSide.New, 2, "b", ["a"], [], "abc", null);
}

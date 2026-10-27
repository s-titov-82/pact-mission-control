using Pact.Core.Remarks;

namespace Pact.Core.Tests.Remarks;

public sealed class RemarkThreadTests
{
	private static readonly RemarkAnchor Anchor = new("src/X.cs", RemarkSide.New, 3, "baz();", ["foo();"], ["qux();"], "abc", "snap");

	[Test]
	public void A_new_remark_is_a_draft() =>
		Thread(User("why?", RemarkDelivery.Draft)).State.ShouldBe(RemarkThreadState.Draft);

	[Test]
	public void A_sending_or_sent_remark_is_sent()
	{
		Thread(User("why?", RemarkDelivery.Sending)).State.ShouldBe(RemarkThreadState.Sent);
		Thread(User("why?", RemarkDelivery.Sent)).State.ShouldBe(RemarkThreadState.Sent);
	}

	[Test]
	public void An_agent_message_after_the_users_answers_it()
	{
		var thread = Thread(User("why?", RemarkDelivery.Sending), Agent("because"));

		thread.State.ShouldBe(RemarkThreadState.Answered);
		thread.Messages[0].Delivery.ShouldBe(RemarkDelivery.Sending);
	}

	[Test]
	public void A_reply_drafted_after_an_answer_is_a_draft() =>
		Thread(User("why?", RemarkDelivery.Sent), Agent("because"), User("and?", RemarkDelivery.Draft))
			.State.ShouldBe(RemarkThreadState.Draft);

	[Test]
	public void Uncertain_delivery_shows_until_the_agent_answers()
	{
		Thread(User("why?", RemarkDelivery.Uncertain)).State.ShouldBe(RemarkThreadState.DeliveryUncertain);
		Thread(User("why?", RemarkDelivery.Uncertain), Agent("because")).State.ShouldBe(RemarkThreadState.Answered);
	}

	[Test]
	public void Resolved_wins_over_everything() =>
		(Thread(User("why?", RemarkDelivery.Draft)) with { IsResolved = true }).State.ShouldBe(RemarkThreadState.Resolved);

	[Test]
	public void The_last_session_is_the_one_the_newest_sent_message_went_to()
	{
		var thread = Thread(
			User("why?", RemarkDelivery.Sent, "s1"),
			Agent("because"),
			User("and?", RemarkDelivery.Sent, "s2"),
			User("draft", RemarkDelivery.Draft));

		thread.LastSessionId.ShouldBe("s2");
		Thread(User("why?", RemarkDelivery.Draft)).LastSessionId.ShouldBeNull();
	}

	[Test]
	public void Several_drafts_are_all_unsent_and_the_newest_is_editable()
	{
		var thread = Thread(User("a", RemarkDelivery.Draft), User("sent", RemarkDelivery.Sent, "s1"), User("b", RemarkDelivery.Draft));

		thread.Unsent.Select(message => message.Text).ShouldBe(["a", "b"]);
		thread.EditableDraft!.Text.ShouldBe("b");
		Thread(User("sent", RemarkDelivery.Sent, "s1")).EditableDraft.ShouldBeNull();
	}

	private static RemarkThread Thread(params RemarkMessage[] messages) => new("C1", Anchor, messages, IsResolved: false);

	private static RemarkMessage User(string text, RemarkDelivery delivery, string? session = null) =>
		new(Guid.NewGuid().ToString("N"), RemarkAuthor.User, text, DateTimeOffset.UnixEpoch, delivery, session);

	private static RemarkMessage Agent(string text) =>
		new(Guid.NewGuid().ToString("N"), RemarkAuthor.Agent, text, DateTimeOffset.UnixEpoch, RemarkDelivery.Sent, null);
}

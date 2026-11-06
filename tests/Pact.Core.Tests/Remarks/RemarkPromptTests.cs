using Pact.Core.Remarks;

namespace Pact.Core.Tests.Remarks;

public sealed class RemarkPromptTests
{
	[TestCase(RemarkSide.New)]
	[TestCase(RemarkSide.Old)]
	public void Historical_prompt_names_exact_target_and_base_and_the_correct_side_path(RemarkSide side)
	{
		var message = User("check historical line");
		RemarkAnchor anchor = new("new.cs", side, 7, "captured", [], [], new string('b', 40), null,
			new string('a', 40), "old.cs");
		var thread = Thread("C7", anchor, message);
		var text = RemarkPrompt.Compose([new RemarkPromptEntry(thread, [message], 7, IsOutdated: false)]);
		text.ShouldContain(new string('a', 40));
		text.ShouldContain(new string('b', 40));
		text.ShouldContain(side == RemarkSide.New ? "new.cs:7" : "old.cs, removed line 7");
		text.ShouldNotContain("uncommitted changes");
		text.ShouldNotContain("of HEAD");
	}
	private const string Header =
		"Remarks from the user on repository changes. Answer every id with pact_reply_remark (id, text; resolve: true when it is done); pact_list_remarks shows them again.";

	[Test]
	public void A_first_remark_has_its_head_line_and_the_quoted_line()
	{
		var message = User("why baz?");
		var thread = Thread("C3", New(118, "baz();"), message);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [message], 121, IsOutdated: false)])
			.ShouldBe($"{Header}\n\nC3 · src/X.cs:121 · why baz?\n> baz();");
	}

	[Test]
	public void An_old_side_remark_names_the_commit()
	{
		var message = User("keep it");
		var thread = Thread("C4", Old(7, "gone();", "abc1234def"), message);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [message], 7, IsOutdated: false)])
			.ShouldBe($"{Header}\n\nC4 · src/X.cs, removed line 7 of base abc1234 · keep it\n> gone();");
	}

	[Test]
	public void The_empty_tree_is_named()
	{
		var message = User("hm");
		var thread = Thread("C5", Old(2, "x", null), message);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [message], 2, IsOutdated: false)])
			.ShouldContain("C5 · src/X.cs, removed line 2 of the empty tree · hm");
	}

	[Test]
	public void An_outdated_thread_is_labelled()
	{
		var message = User("why?");
		var thread = Thread("C6", New(10, "a();"), message);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [message], 10, IsOutdated: true)])
			.ShouldContain("C6 (outdated) · src/X.cs:10 · why?\n> a();");
	}

	[Test]
	public void A_reply_lists_the_earlier_messages_above_the_new_one()
	{
		var reply = User("but why?");
		var thread = Thread("C3", New(5, "baz();"), User("why?", RemarkDelivery.Sent), Agent("because"), reply);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [reply], 5, IsOutdated: false)])
			.ShouldBe($"{Header}\n\nC3 · src/X.cs:5 · reply\n> baz();\nyou: why?\nagent: because\nyou: but why?");
	}

	[Test]
	public void Two_new_messages_go_out_together_in_order()
	{
		var first = User("A");
		var second = User("B");
		var thread = Thread("C1", New(1, "x"), first, second);

		RemarkPrompt.Compose([new RemarkPromptEntry(thread, [first, second], 1, IsOutdated: false)])
			.ShouldBe($"{Header}\n\nC1 · src/X.cs:1 · reply\n> x\nyou: A\nyou: B");
	}

	[Test]
	public void Multi_line_text_is_indented()
	{
		var first = User("line one\nline two");
		var other = User("more");
		var thread = Thread("C1", New(1, "x"), first);
		var second = Thread("C2", New(2, "y"), User("old", RemarkDelivery.Sent), other with { Text = "a\r\nb" });

		RemarkPrompt.Compose(
			[
				new RemarkPromptEntry(thread, [first], 1, IsOutdated: false),
				new RemarkPromptEntry(second, [second.Messages[1]], 2, IsOutdated: false)
			])
			.ShouldBe($"{Header}\n\nC1 · src/X.cs:1 · line one\n  line two\n> x\n\nC2 · src/X.cs:2 · reply\n> y\nyou: old\nyou: a\n  b");
	}

	private static RemarkThread Thread(string id, RemarkAnchor anchor, params RemarkMessage[] messages) =>
		new(id, anchor, messages, IsResolved: false);

	private static RemarkAnchor New(int line, string text) => new("src/X.cs", RemarkSide.New, line, text, [], [], "abc", "snap");

	private static RemarkAnchor Old(int line, string text, string? commit) => new("src/X.cs", RemarkSide.Old, line, text, [], [], commit, null);

	private static RemarkMessage User(string text, RemarkDelivery delivery = RemarkDelivery.Sending) =>
		new(Guid.NewGuid().ToString("N"), RemarkAuthor.User, text, DateTimeOffset.UnixEpoch, delivery, "s1");

	private static RemarkMessage Agent(string text) =>
		new(Guid.NewGuid().ToString("N"), RemarkAuthor.Agent, text, DateTimeOffset.UnixEpoch, RemarkDelivery.Sent, null);
}

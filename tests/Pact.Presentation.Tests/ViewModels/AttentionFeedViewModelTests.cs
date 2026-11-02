using System.Collections.ObjectModel;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Reviews;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class AttentionFeedViewModelTests
{
	[Test]
	public void Manual_bookmark_survives_selection_and_classifier_changes_until_explicitly_cleared()
	{
		var project = Project();
		var session = Session("later");
		project.Sessions.Add(session);
		using AttentionFeedViewModel feed = new([project], Root(), new());
		session.UpdateRecord(session.Record with { IsMarkedForLater = true });
		feed.Items.ShouldHaveSingleItem().ToolTip.ShouldContain("Read later");
		session.SetCurrentTerminal(true);
		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, null, "Working");
		feed.Items.ShouldHaveSingleItem().ToolTip.ShouldContain("Read later");
		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		session.ApplyTerminalStatus(TerminalTabIndicator.None, null, "Done");
		feed.HasItems.ShouldBeTrue();
		session.UpdateRecord(session.Record with { IsMarkedForLater = false });
		feed.HasItems.ShouldBeFalse();
	}

	[TestCase(TerminalTabIndicator.None, false)]
	[TestCase(TerminalTabIndicator.Busy, false)]
	[TestCase(TerminalTabIndicator.Paused, false)]
	[TestCase(TerminalTabIndicator.InputRequested, true)]
	[TestCase(TerminalTabIndicator.Unread, true)]
	[TestCase(TerminalTabIndicator.Failed, true)]
	public void Terminal_attention_follows_existing_indicators(TerminalTabIndicator indicator, bool expected)
	{
		var project = Project();
		var session = Session("s");
		project.Sessions.Add(session);
		using AttentionFeedViewModel feed = new([project], Root(), new());
		session.ApplyTerminalStatus(indicator, null, "reason");
		feed.HasItems.ShouldBe(expected);
		if (expected)
		{
			var card = feed.Items.ShouldHaveSingleItem();
			card.Target.ShouldBeSameAs(session);
			card.Workspace.ShouldBeSameAs(project);
			card.Indicator.ShouldBe(indicator);
			card.ToolTip.ShouldContain("reason");
			feed.Contains(card).ShouldBeTrue();
			session.SetCurrentTerminal(true);
			feed.HasItems.ShouldBeTrue(); // The feed itself cannot acknowledge unread or a question.
			session.ApplyTerminalStatus(TerminalTabIndicator.None, null, "");
			feed.Owns(card).ShouldBeTrue(); // Navigation can still reveal a normally acknowledged target.
			feed.Contains(card).ShouldBeFalse();
		}
		session.ApplyTerminalStatus(TerminalTabIndicator.None, null, "");
		feed.HasItems.ShouldBeFalse();
	}

	[Test]
	public void Unread_and_review_delivery_failure_keep_both_reasons()
	{
		var project = Project();
		var session = Session("author", TerminalTabIndicator.Unread);
		project.Sessions.Add(session);
		using AttentionFeedViewModel feed = new([project], Root(), new());
		session.ApplyReviews([Review("author")]);
		var card = feed.Items.ShouldHaveSingleItem();
		card.Indicator.ShouldBe(TerminalTabIndicator.Failed);
		card.ToolTip.ShouldContain("Unread");
		card.ToolTip.ShouldContain("delivery failed");
	}

	[Test]
	public void Questions_then_errors_then_unread_preserve_tree_order_and_combine_reasons()
	{
		var project = Project();
		var unread = Session("unread", TerminalTabIndicator.Unread);
		var failed = Session("failed", TerminalTabIndicator.Failed);
		var question = Session("question", TerminalTabIndicator.InputRequested);
		project.Sessions.Add(unread);
		project.Sessions.Add(failed);
		project.Sessions.Add(question);
		using AttentionFeedViewModel feed = new([project], Root(), new());
		feed.Items.Select(item => item.Target).ShouldBe([question, failed, unread]);
		question.ApplyReviews([Review("question")]);
		feed.Items.Count.ShouldBe(3);
		feed.Items[0].Indicator.ShouldBe(TerminalTabIndicator.InputRequested);
		feed.Items[0].ToolTip.ShouldContain("delivery failed");
		question.ApplyTerminalStatus(TerminalTabIndicator.None, null, "");
		feed.Items.Select(item => item.Target).ShouldBe([failed, question, unread]);
		question.ApplyReviews([]);
		feed.Items.Select(item => item.Target).ShouldBe([failed, unread]);
	}

	[Test]
	public void Web_unread_is_attention_even_under_activity_but_loading_alone_is_not()
	{
		var project = Project();
		WebPageViewModel page = new(new("web", "Web", "https://example.com", "https://example.com", DateTimeOffset.Now, DateTimeOffset.Now));
		project.WebPages.Add(page);
		using AttentionFeedViewModel feed = new([project], Root(), new());
		page.SetLoading(true);
		feed.HasItems.ShouldBeFalse();
		page.SetMonitorStatus(WebMonitorStatus.Activity);
		feed.HasItems.ShouldBeFalse();
		page.SetMonitorUnread(true);
		feed.Items.ShouldHaveSingleItem().Target.ShouldBeSameAs(page);
		page.SetMonitorUnread(false);
		feed.HasItems.ShouldBeFalse();
	}

	[Test]
	public void Root_pause_and_owner_removal_reject_captured_cards_and_detach_old_sources()
	{
		var root = Root();
		var session = Session("same", TerminalTabIndicator.Unread);
		root.Sessions.Add(session);
		using AttentionFeedViewModel feed = new([], root, new());
		var card = feed.Items.ShouldHaveSingleItem();
		card.OwnerName.ShouldBe("ROOT");
		session.SetManuallyPaused(true);
		feed.Contains(card).ShouldBeFalse();
		feed.HasItems.ShouldBeFalse();
		session.SetManuallyPaused(false);
		feed.Contains(card).ShouldBeTrue();
		root.Sessions.Clear();
		var replacement = Session("same", TerminalTabIndicator.InputRequested);
		root.Sessions.Add(replacement);
		feed.Contains(card).ShouldBeFalse();
		session.ApplyTerminalStatus(TerminalTabIndicator.Failed, null, "late");
		feed.Items.ShouldHaveSingleItem().Target.ShouldBeSameAs(replacement);
	}

	[Test]
	public void Rename_and_project_pause_follow_owner_identity_and_disposal_stops_updates()
	{
		var project = Project();
		var session = Session("s", TerminalTabIndicator.Unread);
		project.Sessions.Add(session);
		ObservableCollection<WorkspaceViewModel> projects = [project];
		using AttentionFeedViewModel feed = new(projects, Root(), new());
		var card = feed.Items.ShouldHaveSingleItem();
		project.UpdateRecord(project.Record with { Name = "Renamed project" });
		session.UpdateRecord(session.Record with { Title = "Renamed session" });
		feed.Items[0].OwnerName.ShouldBe("Renamed project");
		feed.Items[0].Title.ShouldBe("Renamed session");
		feed.Contains(card).ShouldBeTrue();
		projects.Clear(); // Paused workspaces live in a different collection.
		feed.HasItems.ShouldBeFalse();
		feed.Contains(card).ShouldBeFalse();
		session.ApplyTerminalStatus(TerminalTabIndicator.Failed, null, "late");
		feed.HasItems.ShouldBeFalse();
		projects.Add(project);
		feed.HasItems.ShouldBeTrue();
		feed.Dispose();
		feed.HasItems.ShouldBeFalse();
		session.ApplyTerminalStatus(TerminalTabIndicator.InputRequested, null, "later");
		feed.HasItems.ShouldBeFalse();
	}

	[Test]
	public void Enabled_orchestrator_uses_its_own_owner_and_session_replacement()
	{
		OrchestratorSlotViewModel slot = new();
		var session = Session("orchestrator", TerminalTabIndicator.InputRequested);
		slot.AttachSession(session);
		slot.Apply(true, true, true, "Running");
		using AttentionFeedViewModel feed = new([], Root(), slot);
		var card = feed.Items.ShouldHaveSingleItem();
		card.IsOrchestrator.ShouldBeTrue();
		card.OwnerName.ShouldBe("Orchestrator");
		var replacement = Session("orchestrator", TerminalTabIndicator.Unread);
		slot.AttachSession(replacement);
		feed.Contains(card).ShouldBeFalse();
		feed.Items.ShouldHaveSingleItem().Target.ShouldBeSameAs(replacement);
		slot.Apply(true, false, false, "Disabled");
		feed.Contains(card).ShouldBeFalse();
		feed.HasItems.ShouldBeFalse();
	}

	[Test]
	public void Equal_ids_and_titles_in_different_projects_do_not_share_navigation_identity()
	{
		var first = Project();
		var second = Project();
		first.Sessions.Add(Session("same", TerminalTabIndicator.Unread));
		second.Sessions.Add(Session("same", TerminalTabIndicator.Unread));
		ObservableCollection<WorkspaceViewModel> projects = [first, second];
		using AttentionFeedViewModel feed = new(projects, Root(), new());
		var captured = feed.Items[0];
		feed.Items.Count.ShouldBe(2);
		projects.Remove(first);
		feed.Contains(captured).ShouldBeFalse();
		feed.Items.ShouldHaveSingleItem().Workspace.ShouldBeSameAs(second);
	}

	private static RootTabsViewModel Root() => new(RootTabsRecord.CreateDefault());
	private static WorkspaceViewModel Project() => new(new ProjectRecord("p", "Project", @"D:\repo", DateTimeOffset.Now, DateTimeOffset.Now, null), _ => true);
	private static SessionViewModel Session(string id, TerminalTabIndicator indicator = TerminalTabIndicator.None)
	{
		SessionViewModel session = new(new SessionRecord(id, AgentKind.Codex, id, @"D:\repo", "codex", null, SessionStatus.Running, DateTimeOffset.Now, DateTimeOffset.Now));
		session.ApplyTerminalStatus(indicator, null, "reason");
		return session;
	}
	private static ReviewSummary Review(string author) => new("r", "p", author, "reviewer", ReviewState.Running, 1, ReviewRole.Author, "log", 3) { DeliveryIssue = "delivery failed" };
}

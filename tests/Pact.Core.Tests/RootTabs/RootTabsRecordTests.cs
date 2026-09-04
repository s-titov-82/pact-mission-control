using Pact.Core.Agents;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;

namespace Pact.Core.Tests.RootTabs;

public sealed class RootTabsRecordTests
{
	[Test]
	public void Normalize_removes_stale_selection_and_pause_ids()
	{
		var record = RootTabsRecord.CreateDefault() with
		{
			ActiveItemId = "missing",
			PausedItemIds = ["missing"]
		};

		var normalized = record.Normalize();

		normalized.ActiveItemId.ShouldBeNull();
		normalized.PausedItemIds.ShouldBeEmpty();
	}

	[Test]
	public void Normalize_deduplicates_pause_ids_and_retains_owned_items()
	{
		var session = CreateSession("root-session-1");
		var record = RootTabsRecord.CreateDefault() with
		{
			ActiveItemId = session.Id,
			Sessions = [session],
			PausedItemIds = [session.Id, session.Id]
		};

		var normalized = record.Normalize();

		normalized.ActiveItemId.ShouldBe(session.Id);
		normalized.PausedItemIds.ShouldBe([session.Id]);
		normalized.IsPaused(session.Id).ShouldBeTrue();
	}

	[Test]
	public void Normalize_rejects_duplicate_ids_across_item_kinds()
	{
		var record = RootTabsRecord.CreateDefault() with
		{
			Sessions = [CreateSession("duplicate")],
			WebPages = [CreateWebPage("duplicate")]
		};

		Should.Throw<InvalidDataException>(record.Normalize)
			.Message.ShouldContain("duplicate");
	}

	[Test]
	public void CreateDefault_returns_empty_versioned_document()
	{
		var record = RootTabsRecord.CreateDefault();

		record.SchemaVersion.ShouldBe(1);
		record.ActiveItemId.ShouldBeNull();
		record.Sessions.ShouldBeEmpty();
		record.WebPages.ShouldBeEmpty();
		record.PausedItemIds.ShouldBeEmpty();
	}

	private static SessionRecord CreateSession(string id)
	{
		var now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
		return new SessionRecord(
			id,
			AgentKind.Hermes,
			"Hermes",
			@"C:\Workspaces\example",
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
	}

	private static WebPageRecord CreateWebPage(string id)
	{
		var now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");
		return new WebPageRecord(
			id,
			"Jira",
			"https://jira.example.com/",
			"https://jira.example.com/",
			now,
			now);
	}
}

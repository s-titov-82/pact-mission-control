using Pact.Core.Agents;
using Pact.Core.AgentControl;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlToolCatalogTests
{
	[Test]
	public void BuildToolsListResult_ListsAllFiveTools()
	{
		var result = AgentControlToolCatalog.BuildToolsListResult([], []);

		result["tools"]!.AsArray().Select(tool => (string?)tool!["name"])
			.ShouldBe(
				[
					"pact_request_review",
					"pact_append_note",
					"pact_open_web_tab",
					"pact_get_notes",
					"pact_replace_notes"
				],
				ignoreOrder: true);
	}

	[Test]
	public void BuildToolsListResult_declares_revision_safe_notes_schemas()
	{
		var result = AgentControlToolCatalog.BuildToolsListResult([], []);
		var tools = result["tools"]!.AsArray();
		var get = tools.Single(tool => (string?)tool!["name"] == "pact_get_notes");
		var replace = tools.Single(tool => (string?)tool!["name"] == "pact_replace_notes");

		get!["inputSchema"]!["required"].ShouldBeNull();
		replace!["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["text", "expectedRevision"], ignoreOrder: true);
	}

	[Test]
	public void BuildToolsListResult_EmbedsReviewProfileIdsAsEnum()
	{
		ReviewProfile profile = new(
			"claude-opus",
			"Claude Opus reviewer",
			AgentKind.Claude,
			"claude --model opus");

		var result = AgentControlToolCatalog.BuildToolsListResult([], [profile]);

		var review = result["tools"]!.AsArray()
			.Single(tool => (string?)tool!["name"] == "pact_request_review");
		review!["inputSchema"]!["properties"]!["reviewProfileId"]!["enum"]!
			.AsArray().Select(value => (string?)value).ShouldBe(["claude-opus"]);
	}

	[Test]
	public void BuildToolsListResult_MarksReviewArgumentsRequired()
	{
		var result = AgentControlToolCatalog.BuildToolsListResult([], []);

		var review = result["tools"]!.AsArray()
			.Single(tool => (string?)tool!["name"] == "pact_request_review");
		review!["inputSchema"]!["required"]!.AsArray().Select(value => (string?)value)
			.ShouldBe(
				["scenarioId", "reviewProfileId", "target"],
				ignoreOrder: true);
	}
}

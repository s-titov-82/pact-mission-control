using Pact.Core.Agents;

namespace Pact.Infrastructure.Tests.Settings;

public sealed class ReviewerModelCatalogTests
{
	[TestCase("{")]
	[TestCase("""{"models":"unsupported"}""")]
	public async Task MalformedCacheKeepsDefaultChoicesAvailable(string json)
	{
		using var directory = TemporaryDirectory.Create();
		await File.WriteAllTextAsync(Path.Combine(directory.Path, "models_cache.json"), json);
		var state = Path.Combine(directory.Path, ".claude.json");

		var catalog = await ReviewerModelCatalog.LoadAsync(directory.Path, directory.Path, state, CancellationToken.None);

		catalog.Models(AgentKind.Codex).ShouldBeEmpty();
		catalog.Models(AgentKind.Claude).ShouldContain("opus");
		catalog.Efforts(AgentKind.Codex, "unlisted").ShouldContain("medium");
	}

	[Test]
	public async Task LocalMetadataOffersVisibleModelsAndTheirSupportedEfforts()
	{
		using var directory = TemporaryDirectory.Create();
		await File.WriteAllTextAsync(Path.Combine(directory.Path, "models_cache.json"),
			"""
			{"models":[
			  {"slug":"local-model","visibility":"list","supported_reasoning_levels":[{"effort":"medium"},{"effort":"ultra"}]},
			  {"slug":"private-model","visibility":"hide"},
			  {"slug":"bad;command","visibility":"list"}]}
			""");
		var state = Path.Combine(directory.Path, ".claude.json");
		await File.WriteAllTextAsync(state, """{"additionalModelOptionsCache":[{"value":"claude-example[1m]"}]}""");
		await File.WriteAllTextAsync(Path.Combine(directory.Path, "settings.json"), """{"model":"org/opus"}""");

		var catalog = await ReviewerModelCatalog.LoadAsync(directory.Path, directory.Path, state, CancellationToken.None);

		catalog.Models(AgentKind.Codex).ShouldBe(["local-model"]);
		catalog.Efforts(AgentKind.Codex, "local-model").ShouldBe(["medium", "ultra"]);
		catalog.Models(AgentKind.Claude).ShouldContain("claude-example[1m]");
		catalog.Models(AgentKind.Claude).ShouldContain("org/opus");
	}
}

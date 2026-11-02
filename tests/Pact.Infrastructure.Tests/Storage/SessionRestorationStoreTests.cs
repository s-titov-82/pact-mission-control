using Pact.Core.Updates;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class SessionRestorationStoreTests
{
	[Test]
	public async Task Checkpoint_round_trips_and_replaces_the_previous_live_set()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "Settings", "session-state.json");
		SessionRestorationStore store = new(path);
		(await store.LoadAsync(CancellationToken.None)).ShouldBeNull();
		SessionRestorationState state = new(["a", "b"], ["web"], ["b"], new("project", "b"), true);
		await store.SaveAsync(state, CancellationToken.None);
		var loaded = (await new SessionRestorationStore(path).LoadAsync(CancellationToken.None)).ShouldNotBeNull();
		loaded.LiveTerminalIds.ShouldBe(["a", "b"]);
		loaded.LoadedWebPageIds.ShouldBe(["web"]);
		loaded.UnreadTerminalIds.ShouldBe(["b"]);
		loaded.Selection.ShouldBe(state.Selection);
		loaded.OrchestratorWasRunning.ShouldBeTrue();
		await store.SaveAsync(state with { LiveTerminalIds = [], UnreadTerminalIds = [], Selection = null }, CancellationToken.None);
		loaded = (await store.LoadAsync(CancellationToken.None)).ShouldNotBeNull();
		loaded.LiveTerminalIds.ShouldBeEmpty();
		loaded.UnreadTerminalIds.ShouldBeEmpty();
		loaded.Selection.ShouldBeNull();
	}

	[TestCase("{")]
	[TestCase("{\"schemaVersion\":2,\"liveTerminalIds\":[],\"loadedWebPageIds\":[],\"unreadTerminalIds\":[]}")]
	[TestCase("{\"schemaVersion\":1,\"liveTerminalIds\":null}")]
	public async Task Malformed_or_unsupported_checkpoint_is_ignored(string text)
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "session-state.json");
		await File.WriteAllTextAsync(path, text);
		(await new SessionRestorationStore(path).LoadAsync(CancellationToken.None)).ShouldBeNull();
	}
}

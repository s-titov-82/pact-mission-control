using Pact.Core.Orchestrator;

namespace Pact.Infrastructure.Tests.Settings;

public sealed class OrchestratorStoreTests
{
	[Test]
	public async Task Missing_file_returns_the_disabled_default()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		OrchestratorStore store = new(
			Path.Combine(temporaryDirectory.Path, "orchestrator.json"));

		var record = await store.LoadAsync(CancellationToken.None);

		record.Enabled.ShouldBeFalse();
		record.IsProvisioned.ShouldBeFalse();
	}

	[Test]
	public async Task Save_round_trips_every_setting()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "orchestrator.json");
		OrchestratorStore store = new(path);
		var saved = OrchestratorRecord.CreateDefault() with
		{
			Enabled = true,
			LockDetectionEnabled = true,
			LaunchCommand = "hermes -p pact",
			WorkingDirectory = @"C:\repo",
			Credential = "token-1",
			LockPrompt = "locked",
			UnlockPrompt = "unlocked"
		};

		await store.SaveAsync(saved, CancellationToken.None);
		var loaded = await store.LoadAsync(CancellationToken.None);

		loaded.ShouldBe(saved);
	}

	[Test]
	public async Task Unreadable_content_returns_the_disabled_default()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "orchestrator.json");
		await File.WriteAllTextAsync(path, "{ not json");
		OrchestratorStore store = new(path);

		var record = await store.LoadAsync(CancellationToken.None);

		record.Enabled.ShouldBeFalse();
		record.IsProvisioned.ShouldBeFalse();
	}
}

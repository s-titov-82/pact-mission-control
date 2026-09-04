using Pact.Infrastructure.Orchestrator;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class OrchestratorSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Initialize_stores_the_launch_command_and_credential()
	{
		var section = CreateSection(out var store, hermesPresent: true);

		await section.InitializeAsync(CancellationToken.None);

		var saved = await store.LoadAsync(CancellationToken.None);
		saved.LaunchCommand.ShouldContain("hermes");
		saved.Credential.ShouldNotBeNullOrWhiteSpace();
		saved.IsProvisioned.ShouldBeTrue();
	}

	[Test]
	public async Task Initialize_runs_the_slot_from_the_user_home_not_from_the_hermes_root()
	{
		var section = CreateSection(out var store, hermesPresent: true);

		await section.InitializeAsync(CancellationToken.None);

		(await store.LoadAsync(CancellationToken.None)).WorkingDirectory
			.ShouldBe(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
	}

	[Test]
	public async Task Initialize_shows_every_step_including_failures()
	{
		var section = CreateSection(out _, hermesPresent: false);

		await section.InitializeAsync(CancellationToken.None);

		section.ProvisionLog.ShouldNotBeEmpty();
		section.ProvisionLog.ShouldContain(line => line.Contains(
			"hermes",
			StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public async Task Initialize_leaves_the_slot_unprovisioned_when_hermes_is_missing()
	{
		var section = CreateSection(out var store, hermesPresent: false);

		await section.InitializeAsync(CancellationToken.None);

		(await store.LoadAsync(CancellationToken.None)).IsProvisioned.ShouldBeFalse();
	}

	[Test]
	public async Task Reissue_credential_replaces_the_stored_credential()
	{
		var section = CreateSection(out var store, hermesPresent: true);
		await section.InitializeAsync(CancellationToken.None);
		var before = (await store.LoadAsync(CancellationToken.None)).Credential;

		await section.ReissueCredentialAsync(CancellationToken.None);

		(await store.LoadAsync(CancellationToken.None)).Credential.ShouldNotBe(before);
	}

	[Test]
	public async Task Toggling_enabled_persists()
	{
		var section = CreateSection(out var store, hermesPresent: true);
		await section.InitializeAsync(CancellationToken.None);

		section.Enabled = true;
		await section.SaveAsync(CancellationToken.None);

		(await store.LoadAsync(CancellationToken.None)).Enabled.ShouldBeTrue();
	}

	private OrchestratorSectionViewModel CreateSection(
		out OrchestratorStore store,
		bool hermesPresent)
	{
		var root = _temporaryDirectory.Path;
		var hermesHome = Path.Combine(root, ".hermes");
		store = new OrchestratorStore(Path.Combine(root, "orchestrator.json"));
		HermesProvisioner provisioner = new(new FakeHermesCli(
			hermesHome,
			hermesPresent));
		return new OrchestratorSectionViewModel(
			store,
			provisioner,
			hermesHome,
			"http://127.0.0.1:8765/mcp/");
	}

	private sealed class FakeHermesCli(string home, bool installed) : IHermesCli
	{
		public bool IsInstalled() => installed;

		public Task<HermesCliResult> CreateProfileAsync(
			string profileName,
			CancellationToken cancellationToken)
		{
			var path = Path.Combine(home, "profiles", profileName);
			Directory.CreateDirectory(path);
			return Task.FromResult(new HermesCliResult(true, path, "created"));
		}
	}
}

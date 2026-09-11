using Pact.Core.Updates;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;

namespace Pact.Infrastructure.Tests.Updates;

public sealed class SoftRestartTicketStoreTests : IDisposable
{
	private const string RestartId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private const string OtherRestartId =
		"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Pending_ticket_is_written_atomically_to_the_exact_deterministic_path()
	{
		(var store, var paths) = CreateStore();
		var ticket = CreateTicket(RestartId, SoftRestartMode.RestartOnly);

		var path = await store.WriteNewAsync(ticket, CancellationToken.None);

		path.ShouldBe(Path.Combine(
			paths.UpdateHandoffsDirectory,
			RestartId,
			"update-resume.json"));
		File.Exists(path).ShouldBeTrue();
		Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").ShouldBeEmpty();
		await Should.ThrowAsync<IOException>(() =>
			store.WriteNewAsync(ticket, CancellationToken.None));
	}

	[Test]
	public async Task Terminal_restart_ticket_is_consumed_once_without_directory_scan()
	{
		(var store, var paths) = CreateStore();
		await store.WriteNewAsync(
			CreateTicket(RestartId, SoftRestartMode.RestartOnly),
			CancellationToken.None);
		await store.UpdateOutcomeAsync(
			RestartId,
			new SoftRestartOutcome(SoftRestartOutcomeKind.Restarted, null),
			CancellationToken.None);

		var missing = await store.ConsumeExactAsync(
			OtherRestartId,
			new StableReleaseVersion(1, 2, 3),
			CancellationToken.None);
		File.Exists(Path.Combine(
			paths.UpdateHandoffsDirectory,
			RestartId,
			"update-resume.json")).ShouldBeTrue();
		var consumed = await store.ConsumeExactAsync(
			RestartId,
			new StableReleaseVersion(1, 2, 3),
			CancellationToken.None);
		var second = await store.ConsumeExactAsync(
			RestartId,
			new StableReleaseVersion(1, 2, 3),
			CancellationToken.None);

		missing.ShouldBeNull();
		consumed.ShouldNotBeNull().RestartId.ShouldBe(RestartId);
		second.ShouldBeNull();
	}

	[Test]
	public async Task Pending_malformed_and_id_mismatched_tickets_are_quarantined()
	{
		foreach (var kind in new[] { "pending", "malformed", "mismatch" })
		{
			(var store, var paths) = CreateStore(kind);
			var path = await store.WriteNewAsync(
				CreateTicket(RestartId, SoftRestartMode.RestartOnly, paths),
				CancellationToken.None);
			if (kind == "malformed")
			{
				await File.WriteAllTextAsync(path, "{ not-json");
			}
			else if (kind == "mismatch")
			{
				var json = await File.ReadAllTextAsync(path);
				await File.WriteAllTextAsync(
					path,
					json.Replace(RestartId, OtherRestartId, StringComparison.Ordinal));
			}

			(await store.ConsumeExactAsync(
				RestartId,
				new StableReleaseVersion(1, 2, 3),
				CancellationToken.None)).ShouldBeNull();
			File.Exists(path).ShouldBeFalse();
			Directory.GetFiles(Path.GetDirectoryName(path)!, "*.quarantined-*.json")
				.ShouldHaveSingleItem();
			paths.UpdateHandoffsDirectory.ShouldContain(kind);
		}
	}

	[Test]
	public async Task Applied_update_requires_target_version_but_failed_update_restores_old_version()
	{
		(var appliedStore, _) = CreateStore("applied");
		var applied = CreateTicket(RestartId, SoftRestartMode.ApplyUpdate, new AppPaths(
			Path.Combine(_temporaryDirectory.Path, "applied")));
		await appliedStore.WriteNewAsync(applied, CancellationToken.None);
		await appliedStore.UpdateOutcomeAsync(
			RestartId,
			new SoftRestartOutcome(SoftRestartOutcomeKind.UpdateApplied, null),
			CancellationToken.None);
		(await appliedStore.ConsumeExactAsync(
			RestartId,
			new StableReleaseVersion(1, 2, 3),
			CancellationToken.None)).ShouldBeNull();

		(var failedStore, _) = CreateStore("failed");
		var failedTicket = CreateTicket(
			RestartId,
			SoftRestartMode.ApplyUpdate,
			new AppPaths(Path.Combine(_temporaryDirectory.Path, "failed")));
		await failedStore.WriteNewAsync(failedTicket, CancellationToken.None);
		await failedStore.UpdateOutcomeAsync(
			RestartId,
			new SoftRestartOutcome(
				SoftRestartOutcomeKind.UpdateNotApplied,
				"setup-exit"),
			CancellationToken.None);
		var failed = await failedStore.ConsumeExactAsync(
			RestartId,
			new StableReleaseVersion(1, 2, 3),
			CancellationToken.None);

		failed.ShouldNotBeNull().Outcome.Kind.ShouldBe(SoftRestartOutcomeKind.UpdateNotApplied);
		failed.Outcome.ErrorCategory.ShouldBe("setup-exit");
	}

	[Test]
	public async Task Applied_update_at_or_above_target_is_accepted()
	{
		(var store, _) = CreateStore();
		var ticket = CreateTicket(RestartId, SoftRestartMode.ApplyUpdate);
		await store.WriteNewAsync(ticket, CancellationToken.None);
		await store.UpdateOutcomeAsync(
			RestartId,
			new SoftRestartOutcome(SoftRestartOutcomeKind.UpdateApplied, null),
			CancellationToken.None);

		var consumed = await store.ConsumeExactAsync(
			RestartId,
			new StableReleaseVersion(1, 3, 0),
			CancellationToken.None);

		consumed.ShouldNotBeNull();
	}

	[TestCase("short")]
	[TestCase("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
	[TestCase("../0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
	public async Task Restart_id_must_be_64_lowercase_hex(string restartId)
	{
		(var store, _) = CreateStore();

		await Should.ThrowAsync<ArgumentException>(() =>
			store.WriteNewAsync(
				CreateTicket(restartId, SoftRestartMode.RestartOnly),
				CancellationToken.None));
	}

	[Test]
	public async Task Relative_or_cross_root_ticket_paths_are_rejected()
	{
		(var store, var paths) = CreateStore();
		var ticket = CreateTicket(RestartId, SoftRestartMode.RestartOnly);
		SoftRestartTicket[] invalid =
		[
			ticket with { ExecutablePath = @"relative\Pact.App.Avalonia.exe" },
			ticket with { InstallationDirectory = @"relative" },
			ticket with { DataRoot = @"C:\other-root" },
			ticket with { SoftRestartProbeOutputPath = @"relative\probe.json" },
			CreateTicket(RestartId, SoftRestartMode.ApplyUpdate) with
			{
				SetupPath = Path.Combine(paths.SettingsDirectory, "setup.exe")
			}
		];

		foreach (var candidate in invalid)
		{
			await Should.ThrowAsync<ArgumentException>(() =>
				store.WriteNewAsync(candidate, CancellationToken.None));
		}
	}

	[Test]
	public void Generated_restart_id_is_32_random_bytes_as_lowercase_hex()
	{
		var first = SoftRestartTicketStore.CreateRestartId();
		var second = SoftRestartTicketStore.CreateRestartId();

		first.Length.ShouldBe(64);
		first.ShouldMatch("^[0-9a-f]{64}$");
		second.ShouldNotBe(first);
	}

	private (SoftRestartTicketStore Store, AppPaths Paths) CreateStore(string? child = null)
	{
		var root = child is null
			? _temporaryDirectory.Path
			: Path.Combine(_temporaryDirectory.Path, child);
		AppPaths paths = new(root);
		return (new SoftRestartTicketStore(paths, new UpdatePathPolicy(paths)), paths);
	}

	private SoftRestartTicket CreateTicket(
		string restartId,
		SoftRestartMode mode,
		AppPaths? pathOverride = null)
	{
		AppPaths paths = pathOverride ?? new AppPaths(_temporaryDirectory.Path);
		var installation = Path.Combine(paths.RootDirectory, "installation");
		var executable = Path.Combine(installation, "Pact.App.Avalonia.exe");
		StableReleaseVersion? expectedVersion = mode == SoftRestartMode.ApplyUpdate
			? new StableReleaseVersion(1, 3, 0)
			: null;
		var setup = mode == SoftRestartMode.ApplyUpdate
			? Path.Combine(
				paths.UpdatePackagesDirectory,
				"1.3.0",
				"pact-mission-control-1.3.0-win-x64-setup.exe")
			: null;
		return new SoftRestartTicket(
			SchemaVersion: 1,
			restartId,
			mode,
			expectedVersion,
			SourceProcessId: 42,
			executable,
			installation,
			paths.RootDirectory,
			PassDataRoot: true,
			setup,
			SetupSha256: setup is null ? null : new string('a', 64),
			new SoftRestartOutcome(SoftRestartOutcomeKind.Pending, null),
			SoftRestartProbeOutputPath: mode == SoftRestartMode.RestartOnly
				? Path.Combine(paths.TempDirectory, "probe.json")
				: null,
			LiveTerminalIds: ["terminal-1"],
			LoadedWebPageIds: ["web-1"],
			UnreadTerminalIds: ["terminal-1"],
			new SoftRestartSelection("project-1", "terminal-1"),
			OrchestratorWasRunning: true);
	}
}

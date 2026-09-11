using Pact.App.Avalonia.Controllers;
using Pact.Core.Projects;
using Pact.Core.Updates;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;
using Pact.Presentation.ViewModels;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SoftRestartCoordinatorTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Safe_request_writes_ticket_copies_helper_rechecks_and_starts_exact_copy()
	{
		var fixture = CreateFixture(static () => false);
		string? copiedSource = null;
		string? copiedDestination = null;
		string? startedHelper = null;
		string? startedTicket = null;
		SoftRestartCoordinator coordinator = new(
			fixture.Options,
			fixture.Store,
			fixture.Safety,
			fixture.Snapshot,
			() => fixture.Executable,
			() => 42,
			(source, destination, _) =>
			{
				copiedSource = source;
				copiedDestination = destination;
				File.WriteAllText(destination, "helper");
				return Task.CompletedTask;
			},
			(helper, ticket) =>
			{
				startedHelper = helper;
				startedTicket = ticket;
			});

		var result = await coordinator.RequestRestartOnlyAsync(CancellationToken.None);

		result.Started.ShouldBeTrue();
		copiedSource.ShouldBe(Path.Combine(fixture.Installation, "Pact.Updater.exe"));
		startedHelper.ShouldBe(copiedDestination);
		startedTicket.ShouldBe(Path.Combine(
			Path.GetDirectoryName(startedHelper!)!,
			"update-resume.json"));
		File.Exists(startedTicket).ShouldBeTrue();
		Path.GetFileName(Path.GetDirectoryName(startedTicket)!)
			.ShouldMatch("^[0-9a-f]{64}$");
	}

	[Test]
	public async Task Second_safety_failure_removes_ticket_and_never_starts_helper()
	{
		var calls = 0;
		var fixture = CreateFixture(() => ++calls == 2);
		var started = false;
		SoftRestartCoordinator coordinator = new(
			fixture.Options,
			fixture.Store,
			fixture.Safety,
			fixture.Snapshot,
			() => fixture.Executable,
			() => 42,
			(_, destination, _) =>
			{
				File.WriteAllText(destination, "helper");
				return Task.CompletedTask;
			},
			(_, _) => started = true);

		var result = await coordinator.RequestRestartOnlyAsync(CancellationToken.None);

		result.Started.ShouldBeFalse();
		result.Blockers.ShouldHaveSingleItem().Id.ShouldBe("scenario");
		started.ShouldBeFalse();
		Directory.Exists(fixture.Paths.UpdateHandoffsDirectory).ShouldBeTrue();
		Directory.EnumerateFileSystemEntries(fixture.Paths.UpdateHandoffsDirectory)
			.ShouldBeEmpty();
	}

	[Test]
	public async Task ApplyUpdate_writes_the_exact_verified_package_into_the_handoff()
	{
		var fixture = CreateFixture(static () => false);
		var version = new StableReleaseVersion(0, 1, 2);
		var setupDirectory = Path.Combine(
			fixture.Paths.UpdatePackagesDirectory,
			version.ToString());
		var setupPath = Path.Combine(
			setupDirectory,
			$"pact-mission-control-{version}-win-x64-setup.exe");
		PreparedUpdatePackage package = new(
			new UpdateRelease(
				version,
				"v0.1.2",
				new Uri("https://github.com/s-titov-82/pact-mission-control/releases/tag/v0.1.2"),
				new UpdateAsset(Path.GetFileName(setupPath), new Uri("https://github.com/setup.exe"), 42),
				new UpdateAsset("SHA256SUMS.txt", new Uri("https://github.com/SHA256SUMS.txt"), 65)),
			setupPath,
			new string('a', 64),
			"NotSigned");
		string? startedTicket = null;
		SoftRestartCoordinator coordinator = new(
			fixture.Options,
			fixture.Store,
			fixture.Safety,
			fixture.Snapshot,
			() => fixture.Executable,
			() => 42,
			(_, destination, _) =>
			{
				File.WriteAllText(destination, "helper");
				return Task.CompletedTask;
			},
			(_, ticket) => startedTicket = ticket);

		var result = await coordinator.RequestApplyUpdateAsync(
			package,
			CancellationToken.None);

		result.Started.ShouldBeTrue();
		var ticket = JsonSerializer.Deserialize<SoftRestartTicket>(
			await File.ReadAllTextAsync(startedTicket!),
			JsonOptions).ShouldNotBeNull();
		ticket.Mode.ShouldBe(SoftRestartMode.ApplyUpdate);
		ticket.ExpectedTargetVersion.ShouldBe(version);
		ticket.SetupPath.ShouldBe(setupPath);
		ticket.SetupSha256.ShouldBe(new string('a', 64));
		ticket.SoftRestartProbeOutputPath.ShouldBeNull();
	}

	private Fixture CreateFixture(Func<bool> hasActiveScenario)
	{
		var dataRoot = Path.Combine(_temporaryDirectory.Path, "data");
		AppPaths paths = new(dataRoot);
		Directory.CreateDirectory(paths.UpdateHandoffsDirectory);
		var installation = Path.Combine(_temporaryDirectory.Path, "installation");
		var executable = Path.Combine(installation, "Pact.App.Avalonia.exe");
		AppLaunchOptions options = new(
			new AppDataProfile("test", dataRoot),
			PassDataRoot: true,
			EngineProbeOutputPath: null,
			SoftRestartId: null,
			SoftRestartProbeOutputPath: Path.Combine(paths.TempDirectory, "probe.json"));
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		SoftRestartSafetyPolicy safety = new(
			static () => [],
			static () => new Dictionary<string, Pact.Core.Sessions.TerminalClassifierDiagnostics>(),
			hasActiveScenario,
			static () => false);
		SoftRestartSnapshotBuilder snapshot = new(
			viewModel,
			static () => [],
			static () => new Dictionary<string, Pact.Core.Sessions.TerminalClassifierDiagnostics>(),
			static () => []);
		return new Fixture(
			paths,
			installation,
			executable,
			options,
			new SoftRestartTicketStore(paths, new UpdatePathPolicy(paths)),
			safety,
			snapshot);
	}

	private sealed record Fixture(
		AppPaths Paths,
		string Installation,
		string Executable,
		AppLaunchOptions Options,
		SoftRestartTicketStore Store,
		SoftRestartSafetyPolicy Safety,
		SoftRestartSnapshotBuilder Snapshot);

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) =>
			Task.CompletedTask;
		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

	private static JsonSerializerOptions CreateJsonOptions()
	{
		JsonSerializerOptions options = new()
		{
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};
		options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
		return options;
	}
}

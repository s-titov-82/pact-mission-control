using Pact.App.Avalonia.Controllers;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;
using Pact.Presentation.ViewModels;

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
}

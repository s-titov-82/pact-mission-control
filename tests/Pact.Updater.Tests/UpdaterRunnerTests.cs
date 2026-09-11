using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Updates;

namespace Pact.Updater.Tests;

public sealed class UpdaterRunnerTests : IDisposable
{
	private const string RestartId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task RestartOnly_waits_marks_ticket_and_relaunches_only_supported_arguments()
	{
		var fixture = await CreateTicketAsync();
		RecordingProcessLauncher launcher = new(fixture.Ticket.ExecutablePath, waitResult: 0);
		UpdaterRunner runner = new(launcher, TimeSpan.FromSeconds(10));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldBe(0);
		launcher.Events.ShouldBe(["inspect", "wait", "start"]);
		launcher.SetupCalls.ShouldBe(0);
		launcher.StartedExecutable.ShouldBe(fixture.Ticket.ExecutablePath);
		launcher.StartedArguments.ShouldBe(
		[
			"--data-root",
			fixture.Ticket.DataRoot,
			"--soft-restart-id",
			RestartId,
			"--soft-restart-probe-output",
			fixture.Ticket.SoftRestartProbeOutputPath!
		]);
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(SoftRestartOutcomeKind.Restarted, null));
	}

	[Test]
	public async Task Source_timeout_deletes_pending_ticket_without_relaunch_or_force_stop()
	{
		var fixture = await CreateTicketAsync();
		RecordingProcessLauncher launcher = new(fixture.Ticket.ExecutablePath, waitResult: null);
		UpdaterRunner runner = new(launcher, TimeSpan.FromSeconds(10));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		File.Exists(fixture.Path).ShouldBeFalse();
		launcher.Events.ShouldBe(["inspect", "wait"]);
		launcher.StartCalls.ShouldBe(0);
		launcher.StopCalls.ShouldBe(0);
	}

	[Test]
	public async Task Live_source_executable_must_match_ticket_before_waiting()
	{
		var fixture = await CreateTicketAsync();
		RecordingProcessLauncher launcher = new(
			Path.Combine(_temporaryDirectory.Path, "other", "Pact.App.Avalonia.exe"),
			waitResult: 0);
		UpdaterRunner runner = new(launcher, TimeSpan.FromSeconds(10));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		launcher.Events.ShouldBe(["inspect"]);
		File.Exists(fixture.Path).ShouldBeTrue();
	}

	[Test]
	public async Task ApplyUpdate_installs_into_the_running_executable_directory_and_relaunches()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events);
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldBe(0);
		events.ShouldBe(["inspect", "wait", "release", "setup", "start"]);
		gate.Paths.ShouldBe(
		[
			fixture.Ticket.ExecutablePath,
			Path.Combine(fixture.Ticket.InstallationDirectory, "conpty", "OpenConsole.exe")
		]);
		gate.Timeout.ShouldBe(TimeSpan.FromSeconds(20));
		launcher.SetupPath.ShouldBe(fixture.Ticket.SetupPath);
		launcher.SetupArguments.ShouldBe(
		[
			"/SP-",
			"/SILENT",
			"/NORESTART",
			"/NORESTARTAPPLICATIONS",
			"/NOCLOSEAPPLICATIONS",
			$"/DIR={fixture.Ticket.InstallationDirectory}",
			$"/LOG={Path.Combine(Path.GetDirectoryName(fixture.Path)!, "setup.log")}"
		]);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		launcher.StartedExecutable.ShouldBe(fixture.Ticket.ExecutablePath);
		launcher.StartedArguments.ShouldBe(
		[
			"--data-root",
			fixture.Ticket.DataRoot,
			"--soft-restart-id",
			RestartId
		]);
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(SoftRestartOutcomeKind.UpdateApplied, null));
	}

	[Test]
	public async Task ApplyUpdate_hash_mismatch_deletes_untrusted_setup_and_restores_old_Pact()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		await File.WriteAllTextAsync(fixture.Ticket.SetupPath!, "tampered after staging");
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events);
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "start"]);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		launcher.SetupCalls.ShouldBe(0);
		launcher.StartedExecutable.ShouldBe(fixture.Ticket.ExecutablePath);
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"setup-hash-mismatch"));
	}

	[Test]
	public async Task ApplyUpdate_missing_setup_after_source_exit_restores_old_Pact()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		File.Delete(fixture.Ticket.SetupPath!);
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events);
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "start"]);
		launcher.SetupCalls.ShouldBe(0);
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"setup-hash-unavailable"));
	}

	[Test]
	public async Task ApplyUpdate_file_release_timeout_never_starts_setup_and_restores_old_Pact()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events);
		var lockedPath = Path.Combine(
			fixture.Ticket.InstallationDirectory,
			"conpty",
			"OpenConsole.exe");
		RecordingReleaseGate gate = new(
			events,
			new FileReleaseResult(false, [lockedPath]));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "start"]);
		launcher.SetupCalls.ShouldBe(0);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"installed-files-locked"));
	}

	[Test]
	public async Task ApplyUpdate_nonzero_setup_exit_deletes_package_and_restores_old_Pact()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events,
			setupExitCode: 7);
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "setup", "start"]);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"setup-exit-nonzero"));
	}

	[Test]
	public async Task ApplyUpdate_setup_launch_exception_deletes_package_and_restores_old_Pact()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events,
			setupException: new System.ComponentModel.Win32Exception(5));
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "setup", "start"]);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"setup-launch-failed"));
	}

	[Test]
	public async Task ApplyUpdate_relaunch_exception_leaves_a_recoverable_terminal_ticket()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events,
			startException: new System.ComponentModel.Win32Exception(5));
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "setup", "start"]);
		File.Exists(fixture.Ticket.SetupPath!).ShouldBeFalse();
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"relaunch-failed"));
	}

	[Test]
	public async Task ApplyUpdate_failed_setup_and_failed_relaunch_preserve_the_original_failure_ticket()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		List<string> events = [];
		RecordingProcessLauncher launcher = new(
			fixture.Ticket.ExecutablePath,
			waitResult: 0,
			events: events,
			setupExitCode: 7,
			startException: new System.ComponentModel.Win32Exception(5));
		RecordingReleaseGate gate = new(events, new FileReleaseResult(true, []));
		UpdaterRunner runner = new(
			launcher,
			gate,
			TimeSpan.FromSeconds(10),
			TimeSpan.FromSeconds(20));

		var result = await runner.RunAsync(
			new UpdaterOptions(fixture.Path),
			CancellationToken.None);

		result.ShouldNotBe(0);
		events.ShouldBe(["inspect", "wait", "release", "setup", "start"]);
		var terminal = await UpdaterTicketStore.LoadTerminalAsync(
			fixture.Path,
			CancellationToken.None);
		terminal.Outcome.ShouldBe(new SoftRestartOutcome(
			SoftRestartOutcomeKind.UpdateNotApplied,
			"setup-exit-nonzero"));
	}

	[Test]
	public async Task ApplyUpdate_applied_terminal_ticket_rejects_any_error_text()
	{
		var fixture = await CreateApplyUpdateTicketAsync();
		var invalid = fixture.Ticket with
		{
			Outcome = new SoftRestartOutcome(
				SoftRestartOutcomeKind.UpdateApplied,
				@"C:\unredacted\setup.log")
		};
		await File.WriteAllTextAsync(
			fixture.Path,
			JsonSerializer.Serialize(invalid, JsonOptions));

		await Should.ThrowAsync<InvalidDataException>(() =>
			UpdaterTicketStore.LoadTerminalAsync(
				fixture.Path,
				CancellationToken.None));
	}

	private async Task<(string Path, SoftRestartTicket Ticket)> CreateTicketAsync()
	{
		var dataRoot = Path.Combine(_temporaryDirectory.Path, "data");
		var installation = Path.Combine(_temporaryDirectory.Path, "installation");
		var path = Path.Combine(
			dataRoot,
			"Temp",
			"Retained",
			"Updates",
			"Handoffs",
			RestartId,
			"update-resume.json");
		var ticket = new SoftRestartTicket(
			1,
			RestartId,
			SoftRestartMode.RestartOnly,
			ExpectedTargetVersion: null,
			SourceProcessId: 42,
			Path.Combine(installation, "Pact.App.Avalonia.exe"),
			installation,
			dataRoot,
			PassDataRoot: true,
			SetupPath: null,
			SetupSha256: null,
			new SoftRestartOutcome(SoftRestartOutcomeKind.Pending, null),
			Path.Combine(dataRoot, "Temp", "probe.json"),
			["terminal"],
			[],
			[],
			Selection: null,
			OrchestratorWasRunning: false);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ticket, JsonOptions));
		return (path, ticket);
	}

	private async Task<(string Path, SoftRestartTicket Ticket)> CreateApplyUpdateTicketAsync()
	{
		var dataRoot = Path.Combine(_temporaryDirectory.Path, "data");
		var installation = Path.Combine(_temporaryDirectory.Path, "custom installation");
		var setupDirectory = Path.Combine(
			dataRoot,
			"Temp",
			"Retained",
			"Updates",
			"Packages",
			"0.1.2");
		var setupPath = Path.Combine(
			setupDirectory,
			"pact-mission-control-0.1.2-win-x64-setup.exe");
		Directory.CreateDirectory(setupDirectory);
		await File.WriteAllTextAsync(setupPath, "verified setup payload");
		var setupSha256 = Convert.ToHexString(
			SHA256.HashData(await File.ReadAllBytesAsync(setupPath)))
			.ToLowerInvariant();
		var path = Path.Combine(
			dataRoot,
			"Temp",
			"Retained",
			"Updates",
			"Handoffs",
			RestartId,
			"update-resume.json");
		var ticket = new SoftRestartTicket(
			1,
			RestartId,
			SoftRestartMode.ApplyUpdate,
			new StableReleaseVersion(0, 1, 2),
			SourceProcessId: 42,
			Path.Combine(installation, "Pact.App.Avalonia.exe"),
			installation,
			dataRoot,
			PassDataRoot: true,
			setupPath,
			setupSha256,
			new SoftRestartOutcome(SoftRestartOutcomeKind.Pending, null),
			SoftRestartProbeOutputPath: null,
			["terminal"],
			[],
			[],
			Selection: null,
			OrchestratorWasRunning: false);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, JsonSerializer.Serialize(ticket, JsonOptions));
		return (path, ticket);
	}

	private sealed class RecordingProcessLauncher(
		string? executablePath,
		int? waitResult,
		List<string>? events = null,
		int setupExitCode = 0,
		Exception? setupException = null,
		Exception? startException = null)
		: IProcessLauncher
	{
		public List<string> Events { get; } = events ?? [];
		public int SetupCalls { get; private set; }
		public int StartCalls { get; private set; }
		public int StopCalls { get; private set; }
		public string? StartedExecutable { get; private set; }
		public IReadOnlyList<string>? StartedArguments { get; private set; }
		public string? SetupPath { get; private set; }
		public IReadOnlyList<string>? SetupArguments { get; private set; }

		public string? GetExecutablePath(int processId)
		{
			Events.Add("inspect");
			return executablePath;
		}

		public Task<int?> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken token)
		{
			Events.Add("wait");
			return Task.FromResult(waitResult);
		}

		public Task<int> RunSetupAsync(
			string setupPath,
			IReadOnlyList<string> arguments,
			CancellationToken token)
		{
			Events.Add("setup");
			SetupCalls++;
			SetupPath = setupPath;
			SetupArguments = arguments;
			if (setupException is not null)
			{
				throw setupException;
			}
			return Task.FromResult(setupExitCode);
		}

		public void StartPact(string executable, IReadOnlyList<string> arguments)
		{
			Events.Add("start");
			StartCalls++;
			StartedExecutable = executable;
			StartedArguments = arguments;
			if (startException is not null)
			{
				throw startException;
			}
		}
	}

	private sealed class RecordingReleaseGate(
		List<string> events,
		FileReleaseResult result) : IInstalledFileReleaseGate
	{
		public IReadOnlyList<string>? Paths { get; private set; }
		public TimeSpan Timeout { get; private set; }

		public Task<FileReleaseResult> WaitAsync(
			IReadOnlyList<string> paths,
			TimeSpan timeout,
			CancellationToken cancellationToken)
		{
			events.Add("release");
			Paths = paths;
			Timeout = timeout;
			return Task.FromResult(result);
		}
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

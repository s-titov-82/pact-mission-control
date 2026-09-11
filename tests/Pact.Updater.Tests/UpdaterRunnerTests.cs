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

	private sealed class RecordingProcessLauncher(string? executablePath, int? waitResult)
		: IProcessLauncher
	{
		public List<string> Events { get; } = [];
		public int SetupCalls { get; private set; }
		public int StartCalls { get; private set; }
		public int StopCalls { get; private set; }
		public string? StartedExecutable { get; private set; }
		public IReadOnlyList<string>? StartedArguments { get; private set; }

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
			SetupCalls++;
			throw new InvalidOperationException("RestartOnly must not run Setup.");
		}

		public void StartPact(string executable, IReadOnlyList<string> arguments)
		{
			Events.Add("start");
			StartCalls++;
			StartedExecutable = executable;
			StartedArguments = arguments;
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

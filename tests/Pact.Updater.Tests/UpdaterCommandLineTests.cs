using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Updates;

namespace Pact.Updater.Tests;

public sealed class UpdaterCommandLineTests : IDisposable
{
	private const string RestartId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public void Parse_accepts_exactly_one_absolute_ticket_path()
	{
		var path = Path.Combine(_temporaryDirectory.Path, "ticket.json");

		UpdaterCommandLine.Parse([path]).ShouldBe(new UpdaterOptions(Path.GetFullPath(path)));
	}

	[TestCaseSource(nameof(InvalidArguments))]
	public void Parse_rejects_every_other_argument_shape(string[] args) =>
		Should.Throw<ArgumentException>(() => UpdaterCommandLine.Parse(args));

	[Test]
	public async Task Ticket_reader_accepts_only_the_exact_owned_restart_ticket()
	{
		var fixture = CreateTicketFixture();
		await WriteTicketAsync(fixture.Path, fixture.Ticket);

		var loaded = await UpdaterTicketStore.LoadPendingAsync(
			fixture.Path,
			CancellationToken.None);

		loaded.RestartId.ShouldBe(fixture.Ticket.RestartId);
		loaded.ExecutablePath.ShouldBe(fixture.Ticket.ExecutablePath);
		loaded.LiveTerminalIds.ShouldBe(fixture.Ticket.LiveTerminalIds);
		loaded.LoadedWebPageIds.ShouldBe(fixture.Ticket.LoadedWebPageIds);
		loaded.UnreadTerminalIds.ShouldBe(fixture.Ticket.UnreadTerminalIds);
	}

	[Test]
	public async Task Ticket_reader_rejects_wrong_schema_mode_outcome_or_location()
	{
		var fixture = CreateTicketFixture();
		SoftRestartTicket[] invalidTickets =
		[
			fixture.Ticket with { SchemaVersion = 2 },
			fixture.Ticket with { Mode = SoftRestartMode.ApplyUpdate },
			fixture.Ticket with
			{
				Outcome = new SoftRestartOutcome(SoftRestartOutcomeKind.Restarted, null)
			},
			fixture.Ticket with { RestartId = new string('a', 64) },
			fixture.Ticket with { DataRoot = Path.Combine(_temporaryDirectory.Path, "other") }
		];

		foreach (var ticket in invalidTickets)
		{
			await WriteTicketAsync(fixture.Path, ticket);
			await Should.ThrowAsync<InvalidDataException>(() =>
				UpdaterTicketStore.LoadPendingAsync(fixture.Path, CancellationToken.None));
		}

		var wrongPath = Path.Combine(_temporaryDirectory.Path, "update-resume.json");
		await WriteTicketAsync(wrongPath, fixture.Ticket);
		await Should.ThrowAsync<InvalidDataException>(() =>
			UpdaterTicketStore.LoadPendingAsync(wrongPath, CancellationToken.None));
	}

	[Test]
	public async Task Ticket_reader_rejects_executable_and_probe_path_escape()
	{
		var fixture = CreateTicketFixture();
		SoftRestartTicket[] invalidTickets =
		[
			fixture.Ticket with { SourceProcessId = 0 },
			fixture.Ticket with { ExecutablePath = Path.Combine(fixture.DataRoot, "other.exe") },
			fixture.Ticket with { InstallationDirectory = Path.GetPathRoot(fixture.DataRoot)! },
			fixture.Ticket with { SoftRestartProbeOutputPath = Path.Combine(fixture.DataRoot, "outside.json") }
		];

		foreach (var ticket in invalidTickets)
		{
			await WriteTicketAsync(fixture.Path, ticket);
			await Should.ThrowAsync<InvalidDataException>(() =>
				UpdaterTicketStore.LoadPendingAsync(fixture.Path, CancellationToken.None));
		}
	}

	private static IEnumerable<string[]> InvalidArguments()
	{
		yield return [];
		yield return ["relative.json"];
		yield return [@"C:\ticket.json", "extra"];
	}

	private (string DataRoot, string Path, SoftRestartTicket Ticket) CreateTicketFixture()
	{
		var dataRoot = Path.Combine(_temporaryDirectory.Path, "data");
		var ticketPath = Path.Combine(
			dataRoot,
			"Temp",
			"Retained",
			"Updates",
			"Handoffs",
			RestartId,
			"update-resume.json");
		var installation = Path.Combine(_temporaryDirectory.Path, "installation");
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
			Path.Combine(dataRoot, "Temp", "restart-probe.json"),
			["terminal"],
			["web"],
			["terminal"],
			Selection: null,
			OrchestratorWasRunning: false);
		return (dataRoot, ticketPath, ticket);
	}

	private static async Task WriteTicketAsync(string path, SoftRestartTicket ticket)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(ticket, JsonOptions));
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

using Pact.App.Avalonia.Controllers;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Updates;
using Pact.Core.Web;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SoftRestartRestorerTests
{
	[Test]
	public async Task Restoration_is_best_effort_reports_cold_start_and_selects_last()
	{
		List<string> order = [];
		var resumed = new SessionViewModel(Session("resumed", "codex resume abc12345"));
		var cold = new SessionViewModel(Session("cold", null));
		var page = new WebPageViewModel(Web("web"));
		SoftRestartRestorer restorer = new(
			id => id switch { "resumed" => resumed, "cold" => cold, _ => null },
			(session, _) =>
			{
				order.Add($"terminal:{session.Record.Id}");
				return Task.FromResult(ShellProfileCommandPlanner.GetStartPlan(
					session.Record,
					preferResumeCommand: true));
			},
			id => id == "web" ? page : null,
			(item, _) => { order.Add($"web:{item.Record.Id}"); return Task.CompletedTask; },
			(id, _) => { order.Add($"unread:{id}"); return true; },
			_ => { order.Add("orchestrator"); return Task.FromResult(true); },
			(_, _) => { order.Add("selection"); return Task.FromResult(true); },
			static id => id == "orchestrator",
			TimeProvider.System);

		var summary = await restorer.RestoreAsync(Ticket(), CancellationToken.None);

		summary.RestoredTerminalIds.ShouldBe(["resumed", "cold"]);
		summary.ColdStartFallbacks.ShouldBe(["cold:resume-command-unavailable"]);
		summary.RestoredWebPageIds.ShouldBe(["web"]);
		summary.Failures.ShouldBe(["terminal:missing:not-found-or-paused"]);
		summary.OrchestratorRestored.ShouldBeTrue();
		summary.SelectionRestored.ShouldBeTrue();
		order.ShouldBe([
			"terminal:resumed",
			"terminal:cold",
			"unread:cold",
			"web:web",
			"orchestrator",
			"unread:orchestrator",
			"selection"
		]);
	}

	private static SoftRestartTicket Ticket() => new(
		1,
		new string('a', 64),
		SoftRestartMode.RestartOnly,
		ExpectedTargetVersion: null,
		SourceProcessId: 42,
		@"C:\Pact\Pact.App.Avalonia.exe",
		@"C:\Pact",
		@"C:\Data",
		PassDataRoot: true,
		SetupPath: null,
		SetupSha256: null,
		new SoftRestartOutcome(SoftRestartOutcomeKind.Restarted, null),
		SoftRestartProbeOutputPath: null,
		LiveTerminalIds: ["resumed", "missing", "cold", "orchestrator"],
		LoadedWebPageIds: ["web"],
		UnreadTerminalIds: ["cold", "orchestrator"],
		new SoftRestartSelection("project", "resumed"),
		OrchestratorWasRunning: true);

	private static SessionRecord Session(string id, string? resume) => new(
		id,
		AgentKind.Codex,
		id,
		@"C:\Work",
		"codex",
		resume,
		SessionStatus.Stopped,
		DateTimeOffset.UtcNow,
		DateTimeOffset.UtcNow);

	private static WebPageRecord Web(string id) => new(
		id,
		id,
		"https://example.com/",
		"https://example.com/",
		DateTimeOffset.UtcNow,
		DateTimeOffset.UtcNow);
}

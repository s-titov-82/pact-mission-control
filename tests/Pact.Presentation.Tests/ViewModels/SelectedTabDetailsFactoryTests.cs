using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class SelectedTabDetailsFactoryTests
{
	private static readonly DateTimeOffset ObservedAt =
		new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

	[Test]
	public void Terminal_details_explain_classifier_delivery_and_scenario_state()
	{
		SessionViewModel session = new(new SessionRecord(
			"session-1",
			AgentKind.Claude,
			"Author",
			@"D:\Work\Project",
			"claude",
			null,
			SessionStatus.Running,
			ObservedAt,
			ObservedAt));
		session.LockForScenario("b3bfb31f-full-run-id");
		TerminalClassifierDiagnostics diagnostics = new(
			"session-1",
			AgentKind.Claude,
			SessionStatus.Running,
			TerminalScreenVerdictState.InputRequested,
			"Approve changes?",
			TerminalTabIndicator.InputRequested,
			"Approve changes?",
			PromptIsEmpty: false,
			InputRequested: true,
			StatusLine: "Approve changes?",
			ActivityInProgress: false,
			ActivityEpoch: 3,
			HasUnreadCompletion: false,
			Columns: 215,
			Rows: 37,
			LastClassificationAt: ObservedAt,
			PromptEvidence: new TerminalPromptEvidence(
				PromptFound: true,
				BoundaryFound: true,
				NonWhitespaceCharacterCount: 7,
				SeparatorSharesLogicalLine: true));

		ProcessTreeMetricsViewModel metrics = new(
			RootProcessId: 4242,
			ProcessCount: 3,
			WorkingSetBytes: 2 * 1024 * 1024,
			CpuPercent: 5.25,
			SampledAt: ObservedAt.AddSeconds(1));
		var details = SelectedTabDetailsFactory.Create(session, diagnostics, metrics);

		details.Heading.ShouldBe("Selected terminal");
		details.Title.ShouldBe("Author");
		Rows(details)["Agent"].ShouldBe("Claude");
		Rows(details)["Lifecycle"].ShouldBe("Running");
		Rows(details)["Classifier"].ShouldBe("InputRequested — Approve changes?");
		Rows(details)["Indicator"].ShouldBe("InputRequested");
		Rows(details)["Composer"].ShouldBe("Has text");
		Rows(details)["Composer evidence"].ShouldBe(
			"Prompt + wrapped separator · 7 non-whitespace characters");
		Rows(details)["Input request"].ShouldBe("Approve changes?");
		Rows(details)["Activity"].ShouldBe("Idle · epoch 3");
		Rows(details)["Viewport"].ShouldBe("215 × 37");
		Rows(details)["Working directory"].ShouldBe(@"D:\Work\Project");
		Rows(details)["Scenario"].ShouldBe("Locked by b3bfb31f");
		Rows(details)["Classified"].ShouldBe("2026-08-24 09:00:00Z");
		Rows(details)["Process tree"].ShouldBe("PID 4242 · 3 processes");
		Rows(details)["CPU"].ShouldBe("5.3%");
		Rows(details)["Working set"].ShouldBe("2.0 MiB");
		Rows(details)["Metrics sampled"].ShouldBe("2026-08-24 09:00:01Z");
	}

	[Test]
	public void Terminal_details_report_an_unavailable_external_snapshot()
	{
		SessionViewModel session = new(new SessionRecord(
			"session-1",
			AgentKind.Codex,
			"Author",
			@"D:\Work\Project",
			"codex",
			null,
			SessionStatus.Running,
			ObservedAt,
			ObservedAt));
		ProcessTreeMetricsViewModel metrics = new(
			RootProcessId: 4242,
			ProcessCount: 0,
			WorkingSetBytes: 0,
			CpuPercent: null,
			SampledAt: ObservedAt,
			Error: "process exited");

		var details = SelectedTabDetailsFactory.Create(session, diagnostics: null, metrics);

		Rows(details)["External metrics"].ShouldBe("Unavailable — process exited");
		details.Rows.ShouldNotContain(row => row.Label == "CPU");
	}

	[Test]
	public void Web_details_show_full_address_and_live_monitoring_facts()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1",
			"Build",
			"https://builds.test/job/1",
			"https://builds.test/job/1?branch=main#artifacts",
			ObservedAt,
			ObservedAt));
		page.SetBrowserLoaded(true);
		WebMonitorDiagnostics diagnostics = new(
			"web-1",
			"https://builds.test/job/1?branch=main",
			"rule-1",
			"Build status",
			WebMonitorStatus.Unread,
			Activity: false,
			Revision: "build-42",
			Unread: true,
			ObservedAt,
			Attempt: 7,
			NextAttemptAt: ObservedAt.AddSeconds(5),
			Navigating: false,
			LastError: "rule-1 / Evaluation / attempt 6: timed out");

		var details = SelectedTabDetailsFactory.Create(page, diagnostics);

		details.Heading.ShouldBe("Selected web tab");
		details.Title.ShouldBe("Build");
		Rows(details)["Address"].ShouldBe("https://builds.test/job/1?branch=main#artifacts");
		Rows(details)["Browser"].ShouldBe("Loaded");
		Rows(details)["Monitor"].ShouldBe("Unread");
		Rows(details)["Rule"].ShouldBe("Build status (rule-1)");
		Rows(details)["Activity"].ShouldBe("False");
		Rows(details)["Revision"].ShouldBe("build-42");
		Rows(details)["Unread"].ShouldBe("Yes");
		Rows(details)["Observed"].ShouldBe("2026-08-24 09:00:00Z");
		Rows(details)["Polling"].ShouldBe("attempt 7 · next 2026-08-24 09:00:05Z");
		Rows(details)["Navigation"].ShouldBe("Stable");
		Rows(details)["Last error"].ShouldContain("timed out");
	}

	[Test]
	public void Web_details_split_selected_renderers_from_shared_runtime_metrics()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1",
			"Build",
			"https://builds.test",
			"https://builds.test",
			ObservedAt,
			ObservedAt));
		page.SetBrowserLoaded(true);
		WebViewProcessMetricsViewModel metrics = new(
			new(ProcessCount: 2, WorkingSetBytes: 2 * 1024 * 1024, CpuPercent: 5.25),
			new(ProcessCount: 4, WorkingSetBytes: 8 * 1024 * 1024, CpuPercent: 12.5),
			ObservedAt.AddSeconds(2));

		var details = SelectedTabDetailsFactory.Create(
			page,
			diagnostics: null,
			metrics,
			externalMetricsEnabled: true);

		Rows(details)["Page renderers"].ShouldBe("2 processes");
		Rows(details)["Page CPU"].ShouldBe("5.3%");
		Rows(details)["Page working set"].ShouldBe("2.0 MiB");
		Rows(details)["Shared runtime"].ShouldBe("4 processes");
		Rows(details)["Shared CPU"].ShouldBe("12.5%");
		Rows(details)["Shared working set"].ShouldBe("8.0 MiB");
		Rows(details)["Metrics sampled"].ShouldBe("2026-08-24 09:00:02Z");
	}

	[Test]
	public void Web_details_show_aggregate_runtime_when_page_attribution_is_unsupported()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1",
			"Build",
			"https://builds.test",
			"https://builds.test",
			ObservedAt,
			ObservedAt));
		page.SetBrowserLoaded(true);
		WebViewProcessMetricsViewModel metrics = new(
			new(ProcessCount: 0, WorkingSetBytes: 0, CpuPercent: null),
			new(ProcessCount: 6, WorkingSetBytes: 12 * 1024 * 1024, CpuPercent: 8.5),
			ObservedAt.AddSeconds(2),
			PageAttributionAvailable: false);

		var details = SelectedTabDetailsFactory.Create(
			page,
			diagnostics: null,
			metrics,
			externalMetricsEnabled: true);

		Rows(details)["WebView2 runtime"].ShouldBe("6 processes");
		Rows(details)["Runtime CPU"].ShouldBe("8.5%");
		Rows(details)["Runtime working set"].ShouldBe("12.0 MiB");
		Rows(details).ShouldNotContainKey("Page renderers");
	}

	[Test]
	public void Paused_web_page_reports_that_external_metrics_do_not_wake_it()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1",
			"Paused",
			"https://example.test",
			"https://example.test",
			ObservedAt,
			ObservedAt));

		var details = SelectedTabDetailsFactory.Create(
			page,
			diagnostics: null,
			metrics: null,
			externalMetricsEnabled: true);

		Rows(details)["External metrics"].ShouldBe("Not loaded");
	}

	private static Dictionary<string, string> Rows(SelectedTabDetailsViewModel details) =>
		details.Rows.ToDictionary(row => row.Label, row => row.Value, StringComparer.Ordinal);
}

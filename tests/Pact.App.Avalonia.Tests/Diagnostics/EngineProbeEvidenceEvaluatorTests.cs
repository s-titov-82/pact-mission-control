using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class EngineProbeEvidenceEvaluatorTests
{
	[Test]
	public void CompleteHealthyProductionEvidencePassesEveryRequiredPhase()
	{
		WebViewDiagnosticEntry[] terminal =
		[
			Entry(1, "terminal", "adapter-created", handle: true),
			Entry(2, "terminal", "navigation-completed", detail: "source=file:///terminal.html;success=True"),
			Entry(3, "terminal", "webmessage-received", isUiThread: false, detail: "type=ready"),
			Entry(4, "terminal", "webmessage-handled", detail: "type=ready"),
			Entry(5, "terminal", "javascript-ready")
		];
		WebViewDiagnosticEntry[] browser =
		[
			Entry(1, "browser:page", "adapter-created", handle: true),
			Entry(2, "browser:page", "shown", visible: true, attached: true, handle: true),
			Entry(3, "browser:page", "navigation-requested", visible: true, attached: true, handle: true),
			Entry(4, "browser:page", "navigation-completed", detail: "source=file:///terminal.html;success=True"),
			Entry(5, "browser:page", "document-response", detail: "hasTitle=True")
		];

		var result = EngineProbeEvidenceEvaluator.Evaluate(
			terminal,
			browser,
			runtimeStarted: true,
			recentOutput: "PowerShell 7.6.3\r\nPS D:\\work>",
			switchCompleted: true,
			shutdownCompletedOnUiThread: true,
			DomEvidence(),
			processAttributionSucceeded: true);

		result.Passed.Order().ShouldBe(EngineProbeRunner.RequiredProbes.Order());
		result.Decision.ShouldBe("PASS");
	}

	[Test]
	public void Process_attribution_is_required_native_evidence()
	{
		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[],
			[],
			runtimeStarted: false,
			recentOutput: string.Empty,
			switchCompleted: false,
			shutdownCompletedOnUiThread: false,
			DomEvidence(),
			processAttributionSucceeded: true);

		result.Passed.ShouldContain("web-process-attribution");
	}

	[Test]
	public void Evaluate_requires_native_DOM_contract_evidence()
	{
		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[],
			[],
			runtimeStarted: false,
			recentOutput: string.Empty,
			switchCompleted: false,
			shutdownCompletedOnUiThread: false,
			DomEvidence());

		result.Passed.ShouldContain("dom-text");
		result.Passed.ShouldContain("dom-attribute");
		result.Passed.ShouldContain("dom-regex");
		result.Passed.ShouldContain("dom-missing");
		result.Passed.ShouldContain("background-timer");
	}

	[Test]
	public void AdapterDestructionSelectsLifetimeLossDecision()
	{
		WebViewDiagnosticEntry[] terminal =
		[
			Entry(1, "terminal", "adapter-created", handle: true),
			Entry(2, "terminal", "adapter-destroyed", handle: false)
		];

		var result = EngineProbeEvidenceEvaluator.Evaluate(
			terminal,
			[],
			runtimeStarted: false,
			recentOutput: string.Empty,
			switchCompleted: false,
			shutdownCompletedOnUiThread: false,
			DomEvidence());

		result.Decision.ShouldBe("adapter-loss-confirmed");
		result.Passed.ShouldNotContain("runtime-started");
	}

	[Test]
	[TestCase("dom-text", "Stopped")]
	[TestCase("dom-attribute", "41")]
	[TestCase("dom-regex", "124")]
	[TestCase("dom-missing", "unexpected")]
	[TestCase("background-timer", "inactive")]
	public void Incorrect_DOM_contract_evidence_cannot_pass(string key, string actual)
	{
		var evidence = DomEvidence();
		evidence[key] = actual;

		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[],
			[],
			runtimeStarted: false,
			recentOutput: string.Empty,
			switchCompleted: false,
			shutdownCompletedOnUiThread: false,
			evidence);

		result.Passed.ShouldNotContain(key);
		result.Decision.ShouldBe("incomplete-evidence");
	}

	[Test]
	public void BrowserFirstRenderRequiresShowBeforeNavigationAndDocumentResponse()
	{
		WebViewDiagnosticEntry[] browser =
		[
			Entry(1, "browser:page", "adapter-created"),
			Entry(2, "browser:page", "navigation-requested"),
			Entry(3, "browser:page", "shown"),
			Entry(4, "browser:page", "navigation-completed", detail: "success=True"),
			Entry(5, "browser:page", "document-response", detail: "hasTitle=True")
		];

		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[], browser, false, string.Empty, false, false, DomEvidence());

		result.Passed.ShouldNotContain("browser-first-render");
	}

	[Test]
	public void BrowserFirstRenderAllowsAdapterCreationAfterShowButBeforeCompletion()
	{
		WebViewDiagnosticEntry[] browser =
		[
			Entry(1, "browser:page", "shown", visible: true, attached: true, handle: false),
			Entry(2, "browser:page", "navigation-requested", visible: true, attached: true, handle: false),
			Entry(3, "browser:page", "adapter-created", visible: true, attached: true, handle: true),
			Entry(4, "browser:page", "navigation-completed", detail: "success=True"),
			Entry(5, "browser:page", "document-response", detail: "hasTitle=True")
		];

		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[], browser, false, string.Empty, false, false, DomEvidence());

		result.Passed.ShouldContain("browser-first-render");
	}

	[Test]
	public void EscapeOnlyTerminalOutputIsNotCleanOutputEvidence()
	{
		var result = EngineProbeEvidenceEvaluator.Evaluate(
			[], [], true, "\u001b[1t\u001b[c", false, false, DomEvidence());

		result.Passed.ShouldContain("runtime-started");
		result.Passed.ShouldNotContain("first-clean-terminal-output");
	}

	private static Dictionary<string, string?> DomEvidence() =>
		new Dictionary<string, string?>
		{
			["dom-text"] = "Running",
			["dom-attribute"] = "42",
			["dom-regex"] = "123",
			["dom-missing"] = null,
			["background-timer"] = "active",
		};

	private static WebViewDiagnosticEntry Entry(
		long sequence,
		string host,
		string phase,
		bool isUiThread = true,
		bool? visible = null,
		bool? attached = null,
		bool? handle = null,
		string? detail = null) => new(
			sequence,
			DateTimeOffset.UnixEpoch.AddSeconds(sequence),
			host,
			phase,
			isUiThread,
			visible,
			attached,
			handle,
			detail);
}

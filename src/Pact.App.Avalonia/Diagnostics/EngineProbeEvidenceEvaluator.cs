using System.Text.RegularExpressions;

namespace Pact.App.Avalonia.Diagnostics;

internal sealed record EngineProbeEvidenceEvaluation(string[] Passed, string Decision);

internal static partial class EngineProbeEvidenceEvaluator
{
	private static readonly Regex CsiSequence = MyRegex();
	internal static readonly string[] RequiredProbes =
	[
		"navigation-completed",
		"javascript-ready",
		"webmessage-thread-sequence",
		"runtime-started",
		"first-clean-terminal-output",
		"browser-first-render",
		"terminal-browser-terminal-switch",
		"adapter-lifecycle",
		"shutdown-ui-thread",
		"dom-text",
		"dom-attribute",
		"dom-regex",
		"dom-missing",
		"background-timer",
		"web-process-attribution",
	];

	public static EngineProbeEvidenceEvaluation Evaluate(
		IReadOnlyList<WebViewDiagnosticEntry> terminal,
		IReadOnlyList<WebViewDiagnosticEntry> browser,
		bool runtimeStarted,
		string recentOutput,
		bool switchCompleted,
		bool shutdownCompletedOnUiThread,
		IReadOnlyDictionary<string, string?> domEvidence,
		bool processAttributionSucceeded = false)
	{
		ArgumentNullException.ThrowIfNull(domEvidence);
		HashSet<string> passed = new(StringComparer.Ordinal);

		if (terminal.Any(IsSuccessfulNavigation))
		{
			passed.Add("navigation-completed");
		}

		if (terminal.Any(entry => entry.Phase == "javascript-ready"))
		{
			passed.Add("javascript-ready");
		}

		if (HasOrderedReadyMessage(terminal))
		{
			passed.Add("webmessage-thread-sequence");
		}

		if (runtimeStarted)
		{
			passed.Add("runtime-started");
		}

		if (HasCleanTerminalOutput(recentOutput))
		{
			passed.Add("first-clean-terminal-output");
		}

		if (HasFirstBrowserRender(browser))
		{
			passed.Add("browser-first-render");
		}

		if (switchCompleted)
		{
			passed.Add("terminal-browser-terminal-switch");
		}

		if (terminal.Any(IsAdapterCreated) && browser.Any(IsAdapterCreated))
		{
			passed.Add("adapter-lifecycle");
		}

		if (shutdownCompletedOnUiThread)
		{
			passed.Add("shutdown-ui-thread");
		}

		AddMatchingDomEvidence(passed, domEvidence, "dom-text", "Running");
		AddMatchingDomEvidence(passed, domEvidence, "dom-attribute", "42");
		AddMatchingDomEvidence(passed, domEvidence, "dom-regex", "123");
		if (domEvidence.ContainsKey("dom-missing") && domEvidence["dom-missing"] is null)
		{
			passed.Add("dom-missing");
		}

		AddMatchingDomEvidence(passed, domEvidence, "background-timer", "active");
		if (processAttributionSucceeded)
		{
			passed.Add("web-process-attribution");
		}

		var adapterLost = terminal.Concat(browser).Any(entry => entry.Phase == "adapter-destroyed");
		var allRequiredPassed = RequiredProbes.All(passed.Contains);
		var decision = adapterLost
			? "adapter-loss-confirmed"
			: allRequiredPassed
				? "PASS"
				: "incomplete-evidence";
		return new EngineProbeEvidenceEvaluation(passed.Order(StringComparer.Ordinal).ToArray(), decision);
	}

	private static void AddMatchingDomEvidence(
		HashSet<string> passed,
		IReadOnlyDictionary<string, string?> domEvidence,
		string key,
		string expected)
	{
		if (domEvidence.TryGetValue(key, out var actual)
			&& string.Equals(actual, expected, StringComparison.Ordinal))
		{
			passed.Add(key);
		}
	}

	private static bool HasOrderedReadyMessage(IReadOnlyList<WebViewDiagnosticEntry> entries)
	{
		var received = entries.FirstOrDefault(entry =>
			entry.Phase == "webmessage-received" && entry.Detail == "type=ready");
		return received is not null && entries.Any(entry =>
			entry.Sequence > received.Sequence
			&& entry.Phase == "webmessage-handled"
			&& entry.Detail == "type=ready");
	}

	private static bool HasFirstBrowserRender(IReadOnlyList<WebViewDiagnosticEntry> entries)
	{
		var shown = entries.FirstOrDefault(entry =>
			entry.Phase == "shown"
			&& entry.IsVisible == true
			&& entry.IsAttached == true);
		if (shown is null)
		{
			return false;
		}

		var requested = entries.FirstOrDefault(entry =>
			entry.Phase == "navigation-requested" && entry.Sequence > shown.Sequence);
		if (requested is null)
		{
			return false;
		}

		var completed = entries.FirstOrDefault(entry =>
			entry.Sequence > requested.Sequence && IsSuccessfulNavigation(entry));
		return completed is not null
			&& entries.Any(entry =>
				entry.Sequence < completed.Sequence
				&& entry.Phase == "adapter-created"
				&& entry.HasPlatformHandle == true)
			&& entries.Any(entry =>
				entry.Sequence > requested.Sequence
				&& entry.Phase == "document-response"
				&& entry.Detail == "hasTitle=True");
	}

	internal static bool HasCleanTerminalOutput(string output)
	{
		if (string.IsNullOrEmpty(output))
		{
			return false;
		}

		var withoutCsi = CsiSequence.Replace(output, string.Empty);
		return withoutCsi.Any(character => !char.IsWhiteSpace(character) && !char.IsControl(character));
	}

	private static bool IsSuccessfulNavigation(WebViewDiagnosticEntry entry) =>
		entry.Phase == "navigation-completed"
		&& entry.Detail?.Contains("success=True", StringComparison.Ordinal) == true;

	private static bool IsAdapterCreated(WebViewDiagnosticEntry entry) =>
		entry.Phase == "adapter-created";
	[GeneratedRegex("\\x1B\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();
}

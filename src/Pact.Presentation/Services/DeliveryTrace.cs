using System.Diagnostics;
using System.Globalization;
using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// One metadata-only line describing how a prompt delivery went, stage by stage. It never
/// holds prompt or screen text. Stage fields hold milliseconds since the delivery began, or a
/// word saying why the stage did not complete.
/// </summary>
/// <param name="sessionId">Session the prompt was delivered to.</param>
/// <param name="sender">Who sent it.</param>
public sealed class DeliveryTrace(string sessionId, PromptSender sender)
{
	private readonly long _startedAt = Stopwatch.GetTimestamp();

	/// <summary>Whether the session had to be started: none, ok, or not-ready.</summary>
	public string Start { get; set; } = "none";

	/// <summary>Time spent waiting for earlier deliveries to the same session.</summary>
	public string GateWait { get; set; } = "0";

	/// <summary>ok, question, or locked.</summary>
	public string Admission { get; set; } = "-";

	/// <summary>bracketed or clipboard.</summary>
	public string Paste { get; set; } = "-";

	/// <summary>When the paste showed, or timeout.</summary>
	public string Shown { get; set; } = "-";

	/// <summary>When Enter was written.</summary>
	public string Enter { get; set; } = "-";

	/// <summary>When an empty composer was seen, or timeout, no-capture, no-model.</summary>
	public string Confirmed { get; set; } = "-";

	/// <summary>none, or enter when Enter was repeated.</summary>
	public string Repair { get; set; } = "none";

	/// <summary>Whether the user typed into the session during the delivery.</summary>
	public bool Keyboard { get; set; }

	/// <summary>Milliseconds since the delivery began, for the stage fields.</summary>
	public string Elapsed() =>
		((long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

	/// <summary>Formats the line for the application log.</summary>
	public string Format(PromptDeliveryResult result) =>
		$"delivery session={sessionId} sender={sender} start={Start} gate-wait={GateWait} "
		+ $"admission={Admission} paste={Paste} shown={Shown} enter={Enter} confirmed={Confirmed} "
		+ $"repair={Repair} keyboard={(Keyboard ? "yes" : "no")} outcome={result.Outcome}";
}

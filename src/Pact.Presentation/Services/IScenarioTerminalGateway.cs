using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>Provides terminal operations required to trigger and control a visible scenario session.</summary>
public interface IScenarioTerminalGateway
{
	/// <summary>
	/// Submits one short, single-line scenario trigger to a live session.
	/// </summary>
	/// <param name="sessionId">Live session receiving the text.</param>
	/// <param name="prompt">Single-line text to paste and submit.</param>
	/// <param name="confirmDelivery">
	/// Whether to observe the agent for a new busy cycle and recover a dropped submit or paste.
	/// A step trigger must be confirmed; the best-effort terminal-state notice must not be, since
	/// it expects no response and may not be retried.
	/// </param>
	/// <param name="cancellationToken">Cancels the delivery.</param>
	/// <returns>
	/// A structured result distinguishing confirmed activity, an unexplained write, a pending
	/// question, and unsent composer text.
	/// </returns>
	Task<PromptDeliveryResult> SendPromptAsync(
		string sessionId,
		string prompt,
		bool confirmDelivery,
		CancellationToken cancellationToken);

	/// <summary>Sends the agent-specific escape input used to abort active work.</summary>
	Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Returns whether the bound terminal process is currently alive.</summary>
	bool IsSessionAlive(string sessionId);

	/// <summary>Returns a human-readable session title, falling back to its id.</summary>
	string GetSessionLabel(string sessionId);
}

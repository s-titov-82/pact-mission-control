using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>Provides terminal operations required to trigger and control a visible scenario session.</summary>
public interface IScenarioTerminalGateway
{
	/// <summary>
	/// Submits one short, single-line scenario trigger to a live session through the one prompt
	/// sender, as the run that owns the session: only a pending question refuses it, and it is
	/// never pasted twice.
	/// </summary>
	/// <param name="sessionId">Live session receiving the text.</param>
	/// <param name="prompt">Single-line text to paste and submit.</param>
	/// <param name="cancellationToken">Cancels the delivery.</param>
	/// <returns>
	/// How the delivery ended: confirmed, submitted without a composer model, unconfirmed, a
	/// paste that never showed, a pending question, or a session that is not running.
	/// </returns>
	Task<PromptDeliveryResult> SendPromptAsync(
		string sessionId,
		string prompt,
		CancellationToken cancellationToken);

	/// <summary>Sends the agent-specific escape input used to abort active work.</summary>
	Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Returns whether the bound terminal process is currently alive.</summary>
	bool IsSessionAlive(string sessionId);

	/// <summary>Returns a human-readable session title, falling back to its id.</summary>
	string GetSessionLabel(string sessionId);
}

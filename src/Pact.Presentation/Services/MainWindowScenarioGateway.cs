using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>Adapts main-window terminal session operations for scenario execution.</summary>
public sealed class MainWindowScenarioGateway : IScenarioTerminalGateway
{
	private readonly Func<string, string, bool, CancellationToken, Task<PromptDeliveryResult>> _sendPromptAndSubmitAsync;
	private readonly Func<string, SessionViewModel?> _findSession;
	private readonly Func<string, Task> _sendEscapeAsync;
	private readonly Func<string, bool> _isSessionActive;

	/// <summary>
	/// Initializes terminal delegates while preserving run cancellation through trigger submission.
	/// </summary>
	public MainWindowScenarioGateway(
		Func<string, string, bool, CancellationToken, Task<PromptDeliveryResult>> sendPromptAndSubmitAsync,
		Func<string, SessionViewModel?> findSession,
		Func<string, Task> sendEscapeAsync,
		Func<string, bool> isSessionActive)
	{
		ArgumentNullException.ThrowIfNull(sendPromptAndSubmitAsync);
		ArgumentNullException.ThrowIfNull(findSession);
		ArgumentNullException.ThrowIfNull(sendEscapeAsync);
		ArgumentNullException.ThrowIfNull(isSessionActive);

		_sendPromptAndSubmitAsync = sendPromptAndSubmitAsync;
		_findSession = findSession;
		_sendEscapeAsync = sendEscapeAsync;
		_isSessionActive = isSessionActive;
	}

	/// <inheritdoc />
	public Task<PromptDeliveryResult> SendPromptAsync(
		string sessionId,
		string prompt,
		bool confirmDelivery,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(prompt);
		cancellationToken.ThrowIfCancellationRequested();
		return _sendPromptAndSubmitAsync(sessionId, prompt, confirmDelivery, cancellationToken);
	}

	/// <inheritdoc />
	public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		cancellationToken.ThrowIfCancellationRequested();
		return _sendEscapeAsync(sessionId);
	}

	/// <inheritdoc />
	public bool IsSessionAlive(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		return _findSession(sessionId) is not null && _isSessionActive(sessionId);
	}

	/// <inheritdoc />
	public string GetSessionLabel(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		var title = _findSession(sessionId)?.Title;
		return string.IsNullOrWhiteSpace(title) ? sessionId : title;
	}
}

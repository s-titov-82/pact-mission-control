using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// Drives scenario terminals straight through <see cref="SessionRuntimeCoordinator"/>: triggers
/// go out as the run that owns the session, and liveness is the runtime's own controller, so
/// nothing waits for the UI thread.
/// </summary>
/// <param name="coordinator">Owner of every session's input and delivery.</param>
public sealed class RuntimeScenarioGateway(SessionRuntimeCoordinator coordinator) : IScenarioTerminalGateway
{
	private readonly SessionRuntimeCoordinator _coordinator =
		coordinator ?? throw new ArgumentNullException(nameof(coordinator));

	/// <inheritdoc />
	public Task<PromptDeliveryResult> SendPromptAsync(
		string sessionId,
		string prompt,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(prompt);
		return _coordinator.DeliverPromptAsync(
			sessionId,
			prompt,
			submit: true,
			PromptSender.ScenarioOwner,
			startIfNotRunning: false,
			cancellationToken);
	}

	/// <inheritdoc />
	public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		cancellationToken.ThrowIfCancellationRequested();
		return _coordinator.SendEscapeAsync(sessionId);
	}

	/// <inheritdoc />
	public bool IsSessionAlive(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		return _coordinator.TryGetActiveController(sessionId, out _, out _);
	}
}

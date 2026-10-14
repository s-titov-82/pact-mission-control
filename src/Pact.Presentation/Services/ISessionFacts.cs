using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// What prompt delivery needs to know about a session but does not own: its retained screen
/// state, whether a scenario run locks it, and how to start its process the way activating
/// its tab would.
/// </summary>
public interface ISessionFacts
{
	/// <summary>The retained classifier state, or <see langword="null"/> before classification.</summary>
	SessionScreenState? ReadScreen(string sessionId);

	/// <summary>Whether a scenario run currently holds the session's input lock.</summary>
	bool IsScenarioLocked(string sessionId);

	/// <summary>Starts a stopped session in the background, without selecting its tab.</summary>
	Task StartAsync(string sessionId, CancellationToken cancellationToken);
}

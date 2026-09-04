using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>
/// Classifies a stable terminal screen snapshot. Implementations are stateless;
/// the engine owns activity context, so a done verdict while idle is a no-op.
/// </summary>
public interface IAgentScreenProfile
{
	/// <summary>
	/// Classifies agent-specific activity evidence in a stable terminal screen.
	/// </summary>
	/// <param name="screen">The visible terminal screen text to inspect.</param>
	/// <returns>The activity evidence found in the screen.</returns>
	TerminalScreenVerdict Classify(string screen);
}
using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>
/// Applies pure quiescence semantics: any stable screen completes a running
/// activity, while the engine ignores a done verdict when already idle.
/// </summary>
public sealed class QuiescenceScreenProfile : IAgentScreenProfile
{
	/// <summary>
	/// Gets the stateless fallback screen classifier.
	/// </summary>
	public static readonly QuiescenceScreenProfile Instance = new();

	private QuiescenceScreenProfile()
	{
	}

	/// <inheritdoc />
	public TerminalScreenVerdict Classify(string screen) => new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty);
}
namespace Pact.Presentation.Updates;

/// <summary>
/// Identifies whether a release check came from the background cadence or the user.
/// </summary>
public enum UpdateCheckOrigin
{
	/// <summary>The coordinator initiated the check on its schedule.</summary>
	Automatic,
	/// <summary>The user explicitly requested the check.</summary>
	Manual
}

namespace Pact.Core.Sessions;

/// <summary>
/// Selects which command template a launch uses.
/// </summary>
public enum TerminalStartMode
{
	/// <summary>Start a fresh conversation using the profile's command template.</summary>
	Normal,

	/// <summary>
	/// Resume the session's previous conversation. Requires both a resume command template on
	/// the profile and a stored resume id on the session.
	/// </summary>
	Resume
}
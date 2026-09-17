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
	Resume,

	/// <summary>
	/// Resume through the agent's own conversation picker, because the resume command template
	/// is known but the conversation id is not. This is how a session survives a crash, where
	/// no id was ever captured. The agent waits for the user's choice, so the launch itself
	/// starts no activity.
	/// </summary>
	ResumeSelection
}
namespace Pact.Core.Sessions;

/// <summary>Who puts a prompt into a session, which decides what refuses it.</summary>
public enum PromptSender
{
	/// <summary>Anyone putting a prompt into a session; a pending question refuses it.</summary>
	Outsider
}

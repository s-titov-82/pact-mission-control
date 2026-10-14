namespace Pact.Core.Sessions;

/// <summary>Who puts a prompt into a session, which decides what refuses it.</summary>
public enum PromptSender
{
	/// <summary>Anyone except the scenario run that owns the session; a scenario lock refuses it.</summary>
	Outsider,

	/// <summary>The scenario run that holds the session's lock; only a question refuses it.</summary>
	ScenarioOwner
}

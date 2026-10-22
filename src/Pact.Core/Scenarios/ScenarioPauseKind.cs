namespace Pact.Core.Scenarios;

/// <summary>Why a paused run stands still.</summary>
public enum ScenarioPauseKind
{
	/// <summary>The run is not paused.</summary>
	None,

	/// <summary>The user paused the run; every bound session is unlocked until Resume.</summary>
	Manual,

	/// <summary>Pact needs the user; only the stuck session is unlocked.</summary>
	Attention
}

/// <summary>What made Pact pause a run for attention.</summary>
public enum ScenarioAttentionReason
{
	/// <summary>Nothing reached the terminal: a question is open or the session is not running.</summary>
	NothingSent,

	/// <summary>The trigger may have reached the terminal but was not confirmed; it is never resent.</summary>
	Unconfirmed,

	/// <summary>The step watchdog elapsed without a response.</summary>
	Watchdog
}

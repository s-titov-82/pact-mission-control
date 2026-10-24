namespace Pact.Core.Reviews;

/// <summary>Where a review between two agents stands.</summary>
public enum ReviewState
{
	/// <summary>Turns are still passing between the agents.</summary>
	Running,

	/// <summary>The reviewer approved the work.</summary>
	Completed,

	/// <summary>The author answered the last allowed iteration.</summary>
	MaxIterationsReached,

	/// <summary>Pact could not keep the review going, for example a turn was not delivered.</summary>
	Failed,

	/// <summary>A participant left, for example its tab was closed.</summary>
	Aborted
}

/// <summary>Which side of a review a session takes.</summary>
public enum ReviewRole
{
	/// <summary>The agent whose work is reviewed.</summary>
	Author,

	/// <summary>The agent that reviews the work.</summary>
	Reviewer
}

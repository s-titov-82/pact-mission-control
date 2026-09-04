namespace Pact.Core.Scenarios;

/// <summary>Signals that the current scenario step exceeded its watchdog.</summary>
public sealed class ScenarioStepTimeoutException : Exception
{
	/// <summary>Initializes a timeout exception without a diagnostic message.</summary>
	public ScenarioStepTimeoutException()
	{
	}

	/// <summary>Initializes a timeout exception with its diagnostic message.</summary>
	public ScenarioStepTimeoutException(string message)
		: base(message)
	{
	}

	/// <summary>Initializes a timeout exception with a message and underlying failure.</summary>
	public ScenarioStepTimeoutException(string message, Exception innerException)
		: base(message, innerException)
	{
	}
}

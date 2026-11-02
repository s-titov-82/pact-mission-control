namespace Pact.Core.Updates;

/// <summary>Content-free identities used to restore the previous runtime surfaces.</summary>
/// <param name="LiveTerminalIds">Terminal ids whose controllers were live.</param>
/// <param name="LoadedWebPageIds">Browser ids whose hosts were loaded.</param>
/// <param name="UnreadTerminalIds">Unacknowledged terminal completions.</param>
/// <param name="Selection">Global owner and item selection.</param>
/// <param name="OrchestratorWasRunning">Whether the singular orchestrator was live.</param>
public sealed record SessionRestorationState(
	IReadOnlyList<string> LiveTerminalIds,
	IReadOnlyList<string> LoadedWebPageIds,
	IReadOnlyList<string> UnreadTerminalIds,
	SoftRestartSelection? Selection,
	bool OrchestratorWasRunning)
{
	/// <summary>Serialized checkpoint contract version.</summary>
	public int SchemaVersion { get; init; } = 1;
}

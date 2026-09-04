namespace Pact.Core.Orchestrator;

/// <summary>The single orchestrator slot stored in <c>Settings/orchestrator.json</c>.</summary>
/// <remarks>
/// The slot is deliberately singular so every command targeting the orchestrator has one
/// unambiguous destination.
/// </remarks>
/// <param name="SchemaVersion">Document format version.</param>
/// <param name="Enabled">
/// Whether the slot runs. Disabling it preserves its configuration for later use.
/// </param>
/// <param name="LockDetectionEnabled">
/// Whether workstation lock and unlock events are delivered to the slot.
/// </param>
/// <param name="LaunchCommand">Command line that starts the orchestrator agent.</param>
/// <param name="WorkingDirectory">Directory in which the agent starts.</param>
/// <param name="Credential">
/// Durable bearer credential granting cross-session agent-control rights.
/// </param>
/// <param name="LockPrompt">Prompt sent when the workstation locks.</param>
/// <param name="UnlockPrompt">Prompt sent when the workstation unlocks.</param>
public sealed record OrchestratorRecord(
	int SchemaVersion,
	bool Enabled,
	bool LockDetectionEnabled,
	string LaunchCommand,
	string WorkingDirectory,
	string Credential,
	string LockPrompt,
	string UnlockPrompt)
{
	/// <summary>Creates the disabled, unprovisioned first-run document.</summary>
	public static OrchestratorRecord CreateDefault() => new(
		SchemaVersion: 1,
		Enabled: false,
		LockDetectionEnabled: false,
		LaunchCommand: string.Empty,
		WorkingDirectory: string.Empty,
		Credential: string.Empty,
		LockPrompt: "The workstation is locked. Every 5 minutes, run the pact-status-report skill "
			+ "and deliver the result through the configured gateway. Keep doing this until told to stop.",
		UnlockPrompt: "The workstation is unlocked. Stop the recurring status reports.");

	/// <summary>Gets whether the slot has enough configuration to be started.</summary>
	public bool IsProvisioned =>
		!string.IsNullOrWhiteSpace(LaunchCommand)
		&& !string.IsNullOrWhiteSpace(WorkingDirectory)
		&& !string.IsNullOrWhiteSpace(Credential);
}

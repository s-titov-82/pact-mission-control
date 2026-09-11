namespace Pact.Core.Updates;

/// <summary>
/// One immutable, one-time handoff snapshot shared by Pact and its external updater helper.
/// </summary>
/// <param name="SchemaVersion">The exact serialized contract version.</param>
/// <param name="RestartId">The 32-byte random id encoded as 64 lowercase hexadecimal characters.</param>
/// <param name="Mode">Whether Setup is applied or this is a diagnostic restart.</param>
/// <param name="ExpectedTargetVersion">Required target version for update mode.</param>
/// <param name="SourceProcessId">The Pact process the helper must wait for.</param>
/// <param name="ExecutablePath">Normalized Pact executable path.</param>
/// <param name="InstallationDirectory">Normalized parent of the Pact executable.</param>
/// <param name="DataRoot">The exact data root whose state is restored.</param>
/// <param name="PassDataRoot">Whether relaunch must include the explicit data-root option.</param>
/// <param name="SetupPath">Verified Setup path for update mode.</param>
/// <param name="SetupSha256">Verified lowercase Setup SHA-256 for update mode.</param>
/// <param name="Outcome">Pending on creation and terminal after helper processing.</param>
/// <param name="SoftRestartProbeOutputPath">Optional diagnostic evidence path for restart-only mode.</param>
/// <param name="LiveTerminalIds">Terminal ids whose controllers were live.</param>
/// <param name="LoadedWebPageIds">Browser ids whose native hosts were loaded.</param>
/// <param name="UnreadTerminalIds">Terminal unread markers retained only for this handoff.</param>
/// <param name="Selection">The selected owner/item, when one existed.</param>
/// <param name="OrchestratorWasRunning">Whether the orchestrator was live.</param>
public sealed record SoftRestartTicket(
	int SchemaVersion,
	string RestartId,
	SoftRestartMode Mode,
	StableReleaseVersion? ExpectedTargetVersion,
	int SourceProcessId,
	string ExecutablePath,
	string InstallationDirectory,
	string DataRoot,
	bool PassDataRoot,
	string? SetupPath,
	string? SetupSha256,
	SoftRestartOutcome Outcome,
	string? SoftRestartProbeOutputPath,
	IReadOnlyList<string> LiveTerminalIds,
	IReadOnlyList<string> LoadedWebPageIds,
	IReadOnlyList<string> UnreadTerminalIds,
	SoftRestartSelection? Selection,
	bool OrchestratorWasRunning);

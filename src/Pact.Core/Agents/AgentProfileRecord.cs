namespace Pact.Core.Agents;

/// <summary>
/// A launch profile from <c>shell-profiles.json</c> describing how to start one agent
/// or shell in a terminal session.
/// </summary>
/// <param name="Id">Stable key referenced by sessions; must survive profile edits.</param>
/// <param name="Kind">Selects the agent-specific terminal compatibility behavior.</param>
/// <param name="DisplayName">Label shown in launch menus.</param>
/// <param name="CommandTemplate">Command line used to start a fresh session.</param>
/// <param name="ResumeCommandTemplate">
/// Command line used to resume a previous conversation, or <see langword="null"/> when the
/// agent cannot resume. The stored resume id is substituted into this template.
/// </param>
/// <param name="DefaultShell">Shell executable the command is launched through.</param>
public sealed record AgentProfileRecord(
	string Id,
	AgentKind Kind,
	string DisplayName,
	string CommandTemplate,
	string? ResumeCommandTemplate,
	string DefaultShell);

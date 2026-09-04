using System.Text.Json.Serialization;
using Pact.Core.Agents;

namespace Pact.Core.Sessions;

/// <summary>
/// One saved terminal session nested under a project in <c>projects.json</c>.
/// </summary>
/// <param name="Id">Stable key; referenced by the project's active item and by scenario role bindings.</param>
/// <param name="Kind">Agent this session runs, selecting its terminal compatibility behavior.</param>
/// <param name="Title">User-visible tab label.</param>
/// <param name="WorkingDirectory">Directory the process is launched in.</param>
/// <param name="LaunchCommand">
/// Command line for a fresh start. Persisted as <c>startCommand</c> for compatibility with
/// files written before the property was renamed.
/// </param>
/// <param name="ResumeCommand">
/// Command line that resumes the previous conversation, with the agent's resume id already
/// embedded, or <see langword="null"/> when there is nothing to resume.
/// </param>
/// <param name="Status">
/// Last known lifecycle state. <see cref="SessionStatus.Starting"/> and
/// <see cref="SessionStatus.Running"/> cannot outlive the application and are normalized to
/// <see cref="SessionStatus.Stopped"/> on load.
/// </param>
/// <param name="CreatedAt">When the session was created.</param>
/// <param name="LastActiveAt">Last interaction, used for ordering.</param>
public sealed record SessionRecord(
	string Id,
	AgentKind Kind,
	string Title,
	string WorkingDirectory,
	[property: JsonPropertyName("startCommand")] string LaunchCommand,
	string? ResumeCommand,
	SessionStatus Status,
	DateTimeOffset CreatedAt,
	DateTimeOffset LastActiveAt);

using Pact.Core.Agents;

namespace Pact.Core.AgentControl;

/// <summary>
/// The launch arguments that connect a session to Pact's agent tools, keyed by agent kind.
/// </summary>
/// <remarks>
/// The argument syntax is a property of the agent's command-line interface, not of the launch
/// profile that happens to use it: every Claude build reads a JSON file named by
/// <c>--mcp-config</c>, and every Codex build takes <c>-c</c> configuration overrides plus a
/// bearer token from the environment. Keeping the syntax here means one place changes when a CLI
/// changes, and a newly added profile is connected without extra configuration. Kinds absent from
/// this map launch without Pact tools.
/// </remarks>
public static class AgentControlArgumentTemplates
{
	/// <summary>
	/// Returns the argument suffix for <paramref name="kind"/>, or <see langword="null"/> when the
	/// kind has no supported way to receive the connection.
	/// </summary>
	/// <remarks>
	/// Placeholders are materialized at launch: <c>{configPath}</c> writes a session-scoped
	/// configuration file, <c>{endpointUrl}</c> carries the endpoint address, and
	/// <c>{tokenEnvVar}</c> names the environment variable holding the bearer token.
	/// </remarks>
	public static IReadOnlyList<string>? For(AgentKind kind) => kind switch
	{
		AgentKind.Claude => ["--mcp-config", "{configPath}"],
		AgentKind.Codex =>
		[
			"-c",
			"mcp_servers.pact.url={endpointUrl}",
			"-c",
			"mcp_servers.pact.bearer_token_env_var={tokenEnvVar}"
		],
		_ => null
	};
}

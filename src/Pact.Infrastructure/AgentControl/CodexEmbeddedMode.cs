using Pact.Core.Agents;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Keeps a Codex launch that carries Pact overrides in embedded app-server mode.</summary>
/// <remarks>
/// Codex 0.158+ attaches the TUI to a shared background app-server daemon, which cannot accept
/// per-invocation <c>-c</c> overrides. Codex then falls back to embedded mode on its own but prints
/// a warning into the terminal. Pact's overrides (endpoint, token variable, instructions) are
/// session-scoped by design, so the launch requests embedded mode explicitly instead.
/// </remarks>
public static class CodexEmbeddedMode
{
	/// <summary>The Codex option that runs the session without the shared background server.</summary>
	public const string NoDaemonArgument = "--no-daemon";

	/// <summary>
	/// Returns <paramref name="injection"/> with <see cref="NoDaemonArgument"/> prepended when a
	/// Codex launch carries configuration overrides; otherwise returns it unchanged.
	/// </summary>
	/// <remarks>
	/// Codex rejects the option when it is given twice, so a launch command that already names it
	/// is left alone.
	/// </remarks>
	public static LaunchInjection Apply(AgentKind kind, string? launchCommandLine, LaunchInjection injection)
	{
		ArgumentNullException.ThrowIfNull(injection);
		if (kind != AgentKind.Codex
			|| !injection.Arguments.Contains("-c", StringComparer.Ordinal)
			|| NamesNoDaemon(launchCommandLine))
		{
			return injection;
		}

		return injection with { Arguments = [NoDaemonArgument, .. injection.Arguments] };
	}

	private static bool NamesNoDaemon(string? commandLine) =>
		commandLine is not null
		&& commandLine
			.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
			.Any(token => token.Trim('"', '\'') == NoDaemonArgument);
}

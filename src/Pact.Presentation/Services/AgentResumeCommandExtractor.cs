using System.Text.RegularExpressions;
using Pact.Core.Agents;

namespace Pact.Presentation.Services;

/// <summary>
/// Recovers an agent's resume command from its terminal output, so a session can be restarted
/// against the same conversation.
/// </summary>
/// <remarks>
/// Extraction is deliberately strict. Agent TUIs repaint, so captured text can carry escape
/// sequences, stray control characters, and the agent's own usage text. A resume command saved
/// with any of those in it fails the next launch with "The syntax of the command is incorrect",
/// which is why ids must look like real ids rather than any non-whitespace run.
/// </remarks>
public static partial class AgentResumeCommandExtractor
{
	private static readonly Regex AnsiEscapeRegex = MyRegex();

	// Bare control chars (e.g. 0x00-0x08, 0x0E-0x1F, 0x7F) can survive the ANSI
	// strip when a repaint TUI leaves one adjacent to the session id. Left in the
	// saved resume command they make the next launch fail with
	// "The syntax of the command is incorrect."
	private static readonly Regex ControlCharRegex = MyRegex1();

	// The session id is captured as a real id token (alphanumeric plus . _ -),
	// NOT [^\s]+ — otherwise codex's own usage text ("codex resume <id>") is
	// captured verbatim and the placeholder <id> gets saved as the resume command.
	private static readonly Regex CodexResumeRegex = CreateCodexResumeRegex();

	// Saved resume commands may use a wrapper executable (e.g. "claude-personal",
	// "codex-personal") instead of the literal CLI name printed in agent output,
	// so the command-shape checks accept any executable prefix.
	private static readonly Regex CodexResumeCommandRegex = CreateCodexResumeCommandRegex();

	private static readonly Regex CodexSessionIdRegex = CreateCodexSessionIdRegex();

	private static readonly Regex ClaudeResumeRegex = CreateClaudeResumeRegex();

	private static readonly Regex ClaudeResumeCommandRegex = CreateClaudeResumeCommandRegex();

	/// <summary>
	/// Scans terminal output for a resume command.
	/// </summary>
	/// <param name="output">Captured output, which may still contain escape sequences.</param>
	/// <param name="agentKind">Agent to look for, or <see langword="null"/> to try all known forms.</param>
	/// <returns>
	/// The sanitized command, or <see langword="null"/> when the output holds none. Placeholder
	/// text from the agent's own usage message is rejected rather than returned.
	/// </returns>
	public static string? TryExtract(string output, AgentKind? agentKind = null)
	{
		if (string.IsNullOrWhiteSpace(output))
		{
			return null;
		}

		var cleanOutput = AnsiEscapeRegex.Replace(output, string.Empty);

		var command = CodexResumeRegex.Matches(cleanOutput)
			.Concat(ClaudeResumeRegex.Matches(cleanOutput))
			.OrderBy(match => match.Index)
			.Select(match => ControlCharRegex.Replace(match.Groups["command"].Value, string.Empty).Trim())
			.LastOrDefault(command => !string.IsNullOrWhiteSpace(command));
		if (!string.IsNullOrWhiteSpace(command))
		{
			return command;
		}

		if (agentKind != AgentKind.Codex)
		{
			return null;
		}

		return CodexSessionIdRegex.Matches(cleanOutput)
			.OrderBy(match => match.Index)
			.Select(match => ControlCharRegex.Replace(match.Groups["id"].Value, string.Empty).Trim())
			.LastOrDefault(id => !string.IsNullOrWhiteSpace(id)) is { } sessionId
			? $"codex resume {sessionId}"
			: null;
	}

	/// <summary>
	/// Whether <paramref name="command"/> resumes one specific conversation by id. Accepts any
	/// executable name, since saved commands may use a wrapper rather than the literal CLI.
	/// </summary>
	public static bool IsConcreteResumeCommand(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return false;
		}

		var cleanCommand = ControlCharRegex.Replace(
			AnsiEscapeRegex.Replace(command, string.Empty),
			string.Empty);
		return CodexResumeCommandRegex.IsMatch(cleanCommand)
			|| ClaudeResumeCommandRegex.IsMatch(cleanCommand);
	}

	/// <summary>
	/// Reads the conversation id out of a resume command, or <see langword="null"/> when it
	/// carries none.
	/// </summary>
	public static string? TryGetResumeId(string? resumeCommand)
	{
		if (string.IsNullOrWhiteSpace(resumeCommand))
		{
			return null;
		}

		var cleanCommand = Clean(resumeCommand);
		var tokens = Tokenize(cleanCommand);
		return tokens.Length >= 3
			&& IsResumeMarker(tokens[^2])
			&& IsConcreteResumeCommand(cleanCommand)
			? tokens[^1]
			: null;
	}

	/// <summary>
	/// Whether <paramref name="command"/> resumes without naming a conversation — for example
	/// <c>--last</c> or an interactive picker. Such a command stays valid after the stored id is
	/// cleared.
	/// </summary>
	public static bool IsGenericResumeCommand(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return false;
		}

		var tokens = Tokenize(command);
		return tokens.Length >= 2 && IsResumeMarker(tokens[^1]);
	}

	/// <summary>
	/// Replaces the conversation id in a resume command, or removes it when
	/// <paramref name="resumeId"/> is <see langword="null"/>.
	/// </summary>
	/// <returns>
	/// The rewritten command, preserving the user's own executable and flags rather than
	/// regenerating them from the profile.
	/// </returns>
	public static string? SetResumeCommandId(string? resumeCommand, string? resumeId)
	{
		if (string.IsNullOrWhiteSpace(resumeCommand))
		{
			return resumeCommand;
		}

		var trimmed = Clean(resumeCommand);
		var tokens = Tokenize(trimmed);
		if (tokens.Length < 2)
		{
			return trimmed;
		}

		var commandWithoutId = tokens.Length >= 3 && IsResumeMarker(tokens[^2])
			? string.Join(' ', tokens[..^1])
			: trimmed;
		if (string.IsNullOrWhiteSpace(resumeId))
		{
			return commandWithoutId;
		}

		return IsGenericResumeCommand(commandWithoutId)
			? $"{commandWithoutId} {Clean(resumeId)}"
			: commandWithoutId;
	}

	private static string Clean(string command) => ControlCharRegex.Replace(
				AnsiEscapeRegex.Replace(command, string.Empty),
				string.Empty)
			.Trim();

	private static bool IsResumeMarker(string token) => string.Equals(token, "resume", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(token, "--resume", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(token, "-r", StringComparison.OrdinalIgnoreCase);

	private static string[] Tokenize(string command) => command.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
	[GeneratedRegex("\u001B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", RegexOptions.Compiled)]
	private static partial Regex MyRegex();
	[GeneratedRegex(@"\p{Cc}", RegexOptions.Compiled)]
	private static partial Regex MyRegex1();

	[GeneratedRegex(@"(?<!\S)(?<command>codex(?:\.(?:cmd|exe))?\s+resume\s+(?!(?:--last|--all|--include-non-interactive)\b)(?=[A-Za-z0-9._-]*\d)[A-Za-z0-9][A-Za-z0-9._-]{7,})", RegexOptions.IgnoreCase)]
	private static partial Regex CreateCodexResumeRegex();

	[GeneratedRegex(@"^\s*\S+(?:\s+\S+)*\s+resume\s+(?!(?:--last|--all|--include-non-interactive)\b)(?=[A-Za-z0-9._-]*\d)[A-Za-z0-9][A-Za-z0-9._-]{7,}\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex CreateCodexResumeCommandRegex();

	[GeneratedRegex(@"(?im)(?:^|\b)(?:codex\s+)?(?:session|conversation)[\s_-]*id\s*[:=]\s*(?<id>[A-Za-z0-9][A-Za-z0-9._-]{7,})", RegexOptions.IgnoreCase)]
	private static partial Regex CreateCodexSessionIdRegex();

	[GeneratedRegex(@"(?<!\S)(?<command>claude(?:\.(?:cmd|exe))?\s+(?:--resume|-r)\s+[A-Za-z0-9][A-Za-z0-9._-]*)", RegexOptions.IgnoreCase)]
	private static partial Regex CreateClaudeResumeRegex();

	[GeneratedRegex(@"^\s*\S+(?:\s+\S+)*\s+(?:--resume|-r)\s+[A-Za-z0-9][A-Za-z0-9._-]*\s*$", RegexOptions.IgnoreCase)]
	private static partial Regex CreateClaudeResumeCommandRegex();
}
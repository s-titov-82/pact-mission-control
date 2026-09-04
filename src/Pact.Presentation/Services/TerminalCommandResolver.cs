using System.Text;
using Pact.Core.Platform;

namespace Pact.Presentation.Services;

/// <summary>
/// Turns a configured profile command into a command line the pseudo-console can launch,
/// resolving the executable and wrapping it for its file type.
/// </summary>
public sealed class TerminalCommandResolver
{
	private readonly IExecutableLocator _executableLocator;

	/// <summary>
	/// Creates a resolver over <paramref name="executableLocator"/>.
	/// </summary>
	public TerminalCommandResolver(IExecutableLocator executableLocator)
	{
		ArgumentNullException.ThrowIfNull(executableLocator);
		_executableLocator = executableLocator;
	}

	/// <summary>
	/// Resolves a bare executable name against <c>PATH</c>.
	/// </summary>
	/// <returns>
	/// The resolution, or <see langword="null"/> when the command is blank or not installed —
	/// which is how the UI decides a launch profile is unavailable.
	/// </returns>
	public Task<TerminalCommandResolution?> ResolveAsync(string command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return Task.FromResult<TerminalCommandResolution?>(null);
		}

		var resolvedPath = _executableLocator.FindOnPath(command);
		var resolution = string.IsNullOrWhiteSpace(resolvedPath)
			? null
			: new TerminalCommandResolution(command, resolvedPath, BuildLaunchCommand(resolvedPath));
		return Task.FromResult(resolution);
	}

	/// <summary>
	/// Resolves a full command line with arguments into a launchable one.
	/// </summary>
	/// <returns>
	/// The launch command, or <see langword="null"/> for blank input. A command line whose
	/// executable is not a plain shell is routed through PowerShell so profile functions and
	/// aliases still resolve; one that cannot be split is passed through unchanged.
	/// </returns>
	public async Task<string?> ResolveCommandLineAsync(string commandLine)
	{
		if (string.IsNullOrWhiteSpace(commandLine))
		{
			return null;
		}

		var trimmedCommandLine = commandLine.Trim();
		if (!TrySplitCommandLine(trimmedCommandLine, out var executable, out var arguments)
			|| !ShouldResolveExecutable(executable))
		{
			return trimmedCommandLine;
		}

		if (!IsShellExecutable(executable))
		{
			return BuildPowerShellProfileCommand(trimmedCommandLine);
		}

		var resolution = await ResolveAsync(executable);
		return resolution is null
			? BuildPowerShellProfileCommand(trimmedCommandLine)
			: BuildLaunchCommand(resolution.ResolvedPath, arguments);
	}

	/// <summary>
	/// Resolves a command and appends raw argument values only after choosing the direct or
	/// PowerShell-profile execution route.
	/// </summary>
	public async Task<string?> ResolveCommandLineAsync(
		string commandLine,
		IReadOnlyList<string> appendedArguments)
	{
		ArgumentNullException.ThrowIfNull(appendedArguments);
		if (appendedArguments.Count == 0)
		{
			return await ResolveCommandLineAsync(commandLine);
		}

		if (string.IsNullOrWhiteSpace(commandLine))
		{
			return null;
		}

		ValidateArguments(appendedArguments);
		var trimmedCommandLine = commandLine.Trim();
		if (!TrySplitCommandLine(trimmedCommandLine, out var executable, out var arguments))
		{
			return $"{trimmedCommandLine} {RenderWin32Arguments(appendedArguments)}";
		}

		if (!ShouldResolveExecutable(executable))
		{
			return $"{trimmedCommandLine} {RenderWin32Arguments(appendedArguments)}";
		}

		if (!IsShellExecutable(executable))
		{
			return BuildPowerShellProfileCommand(trimmedCommandLine, appendedArguments);
		}

		TerminalCommandResolution? resolution = await ResolveAsync(executable);
		if (resolution is null)
		{
			return BuildPowerShellProfileCommand(trimmedCommandLine, appendedArguments);
		}

		string combinedArguments = string.Join(
			" ",
			new[] { arguments, RenderWin32Arguments(appendedArguments) }
				.Where(value => !string.IsNullOrWhiteSpace(value)));
		return BuildLaunchCommand(resolution.ResolvedPath, combinedArguments);
	}

	/// <summary>Builds a launch command for an executable with no arguments.</summary>
	public static string BuildLaunchCommand(string resolvedPath) => BuildLaunchCommand(resolvedPath, arguments: null);

	/// <summary>
	/// Builds a launch command, wrapping the target according to its extension: batch files run
	/// through <c>cmd.exe</c> and <c>.ps1</c> scripts through <c>pwsh</c>, since neither is
	/// directly executable. Paths are quoted and embedded quotes escaped.
	/// </summary>
	public static string BuildLaunchCommand(string resolvedPath, string? arguments)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(resolvedPath);

		var extension = Path.GetExtension(resolvedPath);
		if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
			|| extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
		{
			return string.IsNullOrWhiteSpace(arguments)
				? $@"cmd.exe /d /s /c """"{EscapeForDoubleQuotes(resolvedPath)}"""""
				: $@"cmd.exe /d /s /c """"{EscapeForDoubleQuotes(resolvedPath)}"" {arguments}""";
		}

		if (extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
		{
			return string.IsNullOrWhiteSpace(arguments)
				? $@"pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{EscapeForDoubleQuotes(resolvedPath)}"""
				: $@"pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File ""{EscapeForDoubleQuotes(resolvedPath)}"" {arguments}";
		}

		return string.IsNullOrWhiteSpace(arguments)
			? $@"""{EscapeForDoubleQuotes(resolvedPath)}"""
			: $@"""{EscapeForDoubleQuotes(resolvedPath)}"" {arguments}";
	}

	/// <summary>
	/// Wraps a command line to run inside PowerShell with the user's profile loaded, so
	/// profile-defined functions and aliases resolve as they would in a normal shell.
	/// </summary>
	/// <remarks>
	/// The wrapped text is one operating-system argument before PowerShell ever parses it, so its
	/// quotes are escaped for the command-line parser that splits arguments. A PowerShell-level
	/// escape does not survive that split: the parser passes the escape character through, ends
	/// the argument at the quote it was meant to protect, and the agent receives a mangled path.
	/// </remarks>
	public static string BuildPowerShellProfileCommand(string commandLine)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

		return $@"pwsh -NoLogo -Command ""{EscapeForCommandLineArgument(commandLine.Trim())}""";
	}

	private static string BuildPowerShellProfileCommand(
		string commandLine,
		IReadOnlyList<string> appendedArguments)
	{
		StringBuilder script = new();
		script.AppendLine("$__pactInjectedArguments = @(");
		foreach (string argument in appendedArguments)
		{
			script
				.Append("    '")
				.Append(argument.Replace("'", "''", StringComparison.Ordinal))
				.AppendLine("'");
		}

		script.AppendLine(")");
		script.Append(commandLine.Trim()).Append(" @__pactInjectedArguments");
		string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.ToString()));
		return $"pwsh -NoLogo -EncodedCommand {encoded}";
	}

	private static void ValidateArguments(IReadOnlyList<string> arguments)
	{
		foreach (string argument in arguments)
		{
			ArgumentNullException.ThrowIfNull(argument);
			if (argument.Contains('\0', StringComparison.Ordinal))
			{
				throw new ArgumentException("A process argument cannot contain NUL.", nameof(arguments));
			}
		}
	}

	private static string RenderWin32Arguments(IReadOnlyList<string> arguments) =>
		string.Join(" ", arguments.Select(QuoteCommandLineArgument));

	private static string QuoteCommandLineArgument(string value) =>
		$@"""{EscapeForCommandLineArgument(value)}""";

	private static string EscapeForDoubleQuotes(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

	/// <summary>
	/// Escapes one value for use inside a quoted operating-system command-line argument: quotes are
	/// prefixed with a backslash, and the backslash runs before a quote or before the closing quote
	/// are doubled so they stay literal.
	/// </summary>
	private static string EscapeForCommandLineArgument(string value)
	{
		StringBuilder builder = new(value.Length + 8);
		var backslashes = 0;
		foreach (var character in value)
		{
			switch (character)
			{
				case '\\':
					backslashes++;
					builder.Append(character);
					break;
				case '"':
					builder.Append('\\', backslashes);
					builder.Append('\\').Append('"');
					backslashes = 0;
					break;
				default:
					backslashes = 0;
					builder.Append(character);
					break;
			}
		}

		builder.Append('\\', backslashes);
		return builder.ToString();
	}

	private static bool TrySplitCommandLine(
		string commandLine,
		out string executable,
		out string arguments)
	{
		executable = string.Empty;
		arguments = string.Empty;
		if (string.IsNullOrWhiteSpace(commandLine))
		{
			return false;
		}

		if (commandLine[0] == '"')
		{
			var endQuoteIndex = commandLine.IndexOf('"', 1);
			if (endQuoteIndex < 0)
			{
				return false;
			}

			executable = commandLine[1..endQuoteIndex];
			arguments = commandLine[(endQuoteIndex + 1)..].TrimStart();
			return !string.IsNullOrWhiteSpace(executable);
		}

		var firstWhitespaceIndex = commandLine.IndexOfAny([' ', '\t', '\r', '\n']);
		if (firstWhitespaceIndex < 0)
		{
			executable = commandLine;
			return true;
		}

		executable = commandLine[..firstWhitespaceIndex];
		arguments = commandLine[(firstWhitespaceIndex + 1)..].TrimStart();
		return !string.IsNullOrWhiteSpace(executable);
	}

	private static bool ShouldResolveExecutable(string executable) =>
		!Path.IsPathFullyQualified(executable)
		&& !executable.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
		&& !executable.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

	private static bool IsShellExecutable(string executable) =>
		executable.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
		|| executable.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase)
		|| executable.Equals("powershell", StringComparison.OrdinalIgnoreCase)
		|| executable.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase)
		|| executable.Equals("cmd", StringComparison.OrdinalIgnoreCase)
		|| executable.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A command resolved to a launchable form.
/// </summary>
/// <param name="RequestedCommand">Command as configured, before resolution.</param>
/// <param name="ResolvedPath">Full path the command resolved to on <c>PATH</c>.</param>
/// <param name="CommandLine">Command line to hand to the pseudo-console, already wrapped and quoted.</param>
public sealed record TerminalCommandResolution(
	string RequestedCommand,
	string ResolvedPath,
	string CommandLine);

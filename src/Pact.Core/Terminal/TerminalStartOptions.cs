namespace Pact.Core.Terminal;

/// <summary>
/// Everything needed to launch one pseudo-console child process.
/// </summary>
/// <param name="CommandLine">Fully resolved command line, already rendered from the profile template.</param>
/// <param name="WorkingDirectory">Directory the child starts in; normally the project root.</param>
/// <param name="Columns">Initial console width in cells; must be positive.</param>
/// <param name="Rows">Initial console height in cells; must be positive.</param>
/// <param name="EnvironmentVariables">
/// Variables layered over the inherited environment, or <see langword="null"/> to inherit
/// unchanged.
/// </param>
public sealed record TerminalStartOptions(
	string CommandLine,
	string WorkingDirectory,
	int Columns,
	int Rows,
	IReadOnlyDictionary<string, string>? EnvironmentVariables = null);
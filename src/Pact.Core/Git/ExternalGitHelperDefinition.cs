namespace Pact.Core.Git;

/// <summary>
/// Root of <c>git-helpers.json</c>: external git GUI integrations plus the git panel's own
/// button commands.
/// </summary>
/// <param name="Helpers">External tools offered when their executable is present.</param>
/// <param name="Commands">
/// Overrides for the git panel's built-in button commands, or <see langword="null"/> to keep
/// the defaults.
/// </param>
public sealed record GitHelpersDocument(
	IReadOnlyList<ExternalGitHelperDefinition> Helpers,
	IReadOnlyList<GitButtonCommandRecord>? Commands = null);

/// <summary>
/// An external git tool and the actions it contributes. A helper is offered only when its
/// executable resolves, so a definition for an uninstalled tool is harmless.
/// </summary>
/// <param name="Id">Stable key surviving edits to the name or path.</param>
/// <param name="Name">Label shown in the git menu.</param>
/// <param name="Executable">Executable name or full path.</param>
/// <param name="WindowsRegistryProbe">
/// Registry lookup for locating the executable when it is not on <c>PATH</c>, or
/// <see langword="null"/> to rely on <c>PATH</c> alone.
/// </param>
/// <param name="Actions">Actions this helper provides.</param>
public sealed record ExternalGitHelperDefinition(
	string Id,
	string Name,
	string Executable,
	WindowsRegistryProbe? WindowsRegistryProbe,
	IReadOnlyList<ExternalGitHelperAction> Actions)
{
	/// <summary>
	/// Expands the <c>{root}</c> and <c>{branch}</c> placeholders in an action's arguments.
	/// </summary>
	/// <returns>
	/// The expanded arguments as separate elements. They are never concatenated into one
	/// command line, so values containing spaces cannot alter the argument boundaries.
	/// </returns>
	public static IReadOnlyList<string> SubstituteArguments(
		ExternalGitHelperAction action,
		string root,
		string branch)
	{
		ArgumentNullException.ThrowIfNull(action);

		return action.Arguments
			.Select(argument => argument
				.Replace("{root}", root, StringComparison.Ordinal)
				.Replace("{branch}", branch, StringComparison.Ordinal))
			.ToArray();
	}
}

/// <summary>
/// Where to find a helper executable in the Windows registry.
/// </summary>
/// <param name="Key">Full registry key path.</param>
/// <param name="Value">Value name under that key holding the executable path.</param>
public sealed record WindowsRegistryProbe(string Key, string Value);

/// <summary>
/// One action contributed by an external git helper.
/// </summary>
/// <param name="Slot">
/// Which git panel slot this fills (for example <c>history</c>, <c>resolve</c>, or
/// <c>custom</c>). The slot decides where the action appears.
/// </param>
/// <param name="Label">Text shown for the action.</param>
/// <param name="Arguments">
/// Argument template passed to the executable, supporting <c>{root}</c> and <c>{branch}</c>.
/// </param>
public sealed record ExternalGitHelperAction(string Slot, string Label, IReadOnlyList<string> Arguments);
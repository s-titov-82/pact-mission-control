using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using Pact.Core.Git;
using Pact.Core.Platform;
using Pact.Infrastructure.Settings;

namespace Pact.Infrastructure.Git;

/// <summary>
/// A helper action whose executable was found on this machine and can therefore be offered.
/// </summary>
/// <param name="HelperName">Owning helper's display name.</param>
/// <param name="Slot">Git panel slot the action fills.</param>
/// <param name="Label">Text shown for the action.</param>
/// <param name="Executable">Resolved full path to the helper executable.</param>
/// <param name="Action">Original definition, still holding the unsubstituted argument template.</param>
public sealed record ResolvedGitHelperAction(
	string HelperName,
	string Slot,
	string Label,
	string Executable,
	ExternalGitHelperAction Action);

/// <summary>
/// Loads <c>git-helpers.json</c> and keeps only the helpers actually installed, so the git menu
/// never offers a tool that cannot start. Results are cached for the resolver's lifetime.
/// </summary>
public sealed class ExternalGitHelperResolver
{
	private readonly string _path;
	private IReadOnlyList<ResolvedGitHelperAction>? _cachedActions;
	private GitButtonCommandSet? _cachedCommands;
	private readonly IExecutableLocator _executableLocator;

	/// <summary>
	/// Creates a resolver over the helpers file at <paramref name="path"/>.
	/// </summary>
	public ExternalGitHelperResolver(string path, IExecutableLocator executableLocator)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(executableLocator);

		_path = path;
		_executableLocator = executableLocator;
	}

	/// <summary>
	/// Returns the actions whose helper executable resolves on this machine.
	/// </summary>
	/// <returns>
	/// The available actions, empty when the file is missing or no helper is installed. A
	/// missing or unreadable file is treated as "no helpers configured" rather than an error.
	/// </returns>
	public async Task<IReadOnlyList<ResolvedGitHelperAction>> ResolveAsync(CancellationToken cancellationToken)
	{
		if (_cachedActions is not null)
		{
			return _cachedActions;
		}

		var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
		if (document is null)
		{
			_cachedActions = [];
			return _cachedActions;
		}

		List<ResolvedGitHelperAction> actions = [];
		foreach (var helper in document.Helpers ?? [])
		{
			var executable = ResolveExecutable(helper);
			if (string.IsNullOrWhiteSpace(executable))
			{
				continue;
			}

			foreach (var action in helper.Actions)
			{
				actions.Add(new ResolvedGitHelperAction(
					helper.Name,
					action.Slot,
					action.Label,
					executable,
					action));
			}
		}

		_cachedActions = actions;
		return _cachedActions;
	}

	/// <summary>
	/// Loads the popup button commands from the same file's "commands" array. A missing array or
	/// an unreadable file yields the built-in defaults, so the popup buttons always work.
	/// </summary>
	public async Task<GitButtonCommandSet> LoadCommandsAsync(CancellationToken cancellationToken)
	{
		if (_cachedCommands is not null)
		{
			return _cachedCommands;
		}

		var document = await LoadDocumentAsync(cancellationToken).ConfigureAwait(false);
		_cachedCommands = GitButtonCommandSet.Create(document?.Commands);
		return _cachedCommands;
	}

	/// <summary>
	/// Starts the helper for <paramref name="action"/>, substituting <c>{root}</c> and
	/// <c>{branch}</c> into its arguments. The helper runs detached: Pact does not wait for it
	/// or capture its output.
	/// </summary>
	public static void Launch(ResolvedGitHelperAction action, string root, string branch)
	{
		ArgumentNullException.ThrowIfNull(action);
		ArgumentException.ThrowIfNullOrWhiteSpace(root);

		ProcessStartInfo startInfo = new(action.Executable)
		{
			UseShellExecute = false,
			CreateNoWindow = false
		};
		foreach (var argument in ExternalGitHelperDefinition.SubstituteArguments(action.Action, root, branch))
		{
			startInfo.ArgumentList.Add(argument);
		}

		_ = Process.Start(startInfo);
	}

	private async Task<GitHelpersDocument?> LoadDocumentAsync(CancellationToken cancellationToken)
	{
		try
		{
			if (!File.Exists(_path))
			{
				return new GitHelpersDocument([]);
			}

			var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
			return JsonSerializer.Deserialize<GitHelpersDocument>(json, SettingsFileStore.JsonOptions);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			Debug.WriteLine($"Git helper definitions could not be loaded from '{_path}': {ex}");
			return null;
		}
	}

	private string? ResolveExecutable(ExternalGitHelperDefinition helper)
	{
		if (Path.IsPathFullyQualified(helper.Executable) && File.Exists(helper.Executable))
		{
			return helper.Executable;
		}

		var registryPath = ResolveRegistryExecutable(helper.WindowsRegistryProbe);
		if (!string.IsNullOrWhiteSpace(registryPath))
		{
			return registryPath;
		}

		return _executableLocator.FindOnPath(helper.Executable);
	}

	private static string? ResolveRegistryExecutable(WindowsRegistryProbe? probe)
	{
		if (probe is null || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
		{
			return null;
		}

		foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
		{
			try
			{
				using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
				using var key = baseKey.OpenSubKey(probe.Key);
				var value = key?.GetValue(probe.Value) as string;
				if (!string.IsNullOrWhiteSpace(value) && File.Exists(value))
				{
					return value;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
			{
				Debug.WriteLine($"Git helper registry probe failed for '{probe.Key}': {ex}");
			}
		}

		return null;
	}
}

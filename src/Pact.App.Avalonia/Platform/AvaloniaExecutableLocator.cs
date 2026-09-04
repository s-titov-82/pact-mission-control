using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

internal sealed class AvaloniaExecutableLocator : IExecutableLocator
{
	private static readonly string[] PreferredExtensions = [".exe", ".com", ".cmd", ".bat", ".ps1"];

	public string? FindOnPath(string executableName)
	{
		if (string.IsNullOrWhiteSpace(executableName))
		{
			return null;
		}

		if (Path.IsPathFullyQualified(executableName))
		{
			return File.Exists(executableName) ? Path.GetFullPath(executableName) : null;
		}

		var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
			.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		var extensions = GetExtensions(executableName);
		foreach (var directoryValue in pathEntries)
		{
			var directory = directoryValue.Trim('"');
			foreach (var extension in extensions)
			{
				var candidate = Path.Combine(directory, executableName + extension);
				if (File.Exists(candidate))
				{
					return Path.GetFullPath(candidate);
				}
			}
		}

		return null;
	}

	private static string[] GetExtensions(string executableName)
	{
		if (Path.HasExtension(executableName))
		{
			return [string.Empty];
		}

		var pathExtensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
			.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(extension => extension.StartsWith('.') ? extension : $".{extension}")
			.ToHashSet(StringComparer.OrdinalIgnoreCase);
		return PreferredExtensions
			.Where(pathExtensions.Contains)
			.Concat(pathExtensions.Except(PreferredExtensions, StringComparer.OrdinalIgnoreCase))
			.DefaultIfEmpty(".exe")
			.ToArray();
	}
}
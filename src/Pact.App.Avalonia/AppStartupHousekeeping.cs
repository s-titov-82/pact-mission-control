using Pact.App.Avalonia.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia;

internal static class AppStartupHousekeeping
{
	internal static void Run(AppPaths appPaths)
	{
		ArgumentNullException.ThrowIfNull(appPaths);
		DataRootHousekeeping.Prepare(appPaths);
		DataRootHousekeeping.ClearSessionTemp(appPaths);
		ClearConsumedUpdateHandoffs(appPaths);
		new RotatingAppLog(appPaths.LogsDirectory).ApplyRetention();
	}

	private static void ClearConsumedUpdateHandoffs(AppPaths appPaths)
	{
		if (!Directory.Exists(appPaths.UpdateHandoffsDirectory))
		{
			return;
		}

		foreach (var directory in Directory.EnumerateDirectories(appPaths.UpdateHandoffsDirectory))
		{
			var restartId = Path.GetFileName(directory);
			if (!IsRestartId(restartId)
				|| IsReparsePoint(directory)
				|| File.Exists(Path.Combine(directory, "update-resume.json")))
			{
				continue;
			}

			DeleteKnownFile(Path.Combine(directory, "setup.log"));
			DeleteKnownFile(Path.Combine(directory, "Pact.Updater.exe"));
			try
			{
				if (!Directory.EnumerateFileSystemEntries(directory).Any())
				{
					Directory.Delete(directory);
				}
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}

	private static bool IsReparsePoint(string directory)
	{
		try
		{
			return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}

	private static void DeleteKnownFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private static bool IsRestartId(string value) =>
		value.Length == 64
		&& value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

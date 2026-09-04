namespace Pact.Infrastructure.Storage;

/// <summary>Creates the data-root contract and removes only explicitly disposable temporary data.</summary>
public static class DataRootHousekeeping
{
	/// <summary>
	/// Creates exactly the four supported top-level directories and removes direct pre-split Temp children.
	/// Existing Session and Retained ownership directories, including their contents, are preserved.
	/// </summary>
	public static void Prepare(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);

		Directory.CreateDirectory(paths.SettingsDirectory);
		Directory.CreateDirectory(paths.WebViewDirectory);
		Directory.CreateDirectory(paths.LogsDirectory);
		Directory.CreateDirectory(paths.TempDirectory);
		ClearLegacyTempChildren(paths);
		Directory.CreateDirectory(paths.SessionTempDirectory);
		Directory.CreateDirectory(paths.RetainedTempDirectory);
	}

	/// <summary>
	/// Best-effort deletes each direct child of the disposable session Temp subtree and leaves it available.
	/// Retained Temp data is never enumerated or deleted.
	/// </summary>
	public static void ClearSessionTemp(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);

		if (Directory.Exists(paths.SessionTempDirectory))
		{
			foreach (var entry in Directory.EnumerateFileSystemEntries(paths.SessionTempDirectory))
			{
				try
				{
					if (Directory.Exists(entry))
					{
						Directory.Delete(entry, recursive: true);
					}
					else
					{
						File.Delete(entry);
					}
				}
				catch (IOException)
				{
					// A live handle must not prevent the remaining temporary data from being cleared.
				}
				catch (UnauthorizedAccessException)
				{
					// Best effort: startup and shutdown must remain available with a locked child.
				}
			}
		}

		Directory.CreateDirectory(paths.SessionTempDirectory);
	}

	private static void ClearLegacyTempChildren(AppPaths paths)
	{
		foreach (var entry in Directory.EnumerateFileSystemEntries(paths.TempDirectory))
		{
			var name = Path.GetFileName(entry);
			if (string.Equals(name, "Session", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(name, "Retained", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			try
			{
				if (Directory.Exists(entry))
				{
					Directory.Delete(entry, recursive: true);
				}
				else
				{
					File.Delete(entry);
				}
			}
			catch (IOException)
			{
				// A stale legacy handle must not prevent the data root from opening.
			}
			catch (UnauthorizedAccessException)
			{
				// Best effort cleanup retries the next time the process starts.
			}
		}
	}
}
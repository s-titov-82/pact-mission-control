using Pact.Infrastructure.Storage;

namespace Pact.ProfileTool;

/// <summary>Copies a Pact data-root profile to another directory for
/// debugging: durable settings and notes, excluding logs and temporary files.
/// Both roots are leased for the duration of the copy.</summary>
public static class AppProfileSnapshotCopier
{
	/// <summary>Copies <paramref name="source"/> into <paramref name="destination"/>
	/// via a staging directory, so the destination is either complete or untouched.
	/// The destination must be empty unless <paramref name="replace"/> is set.</summary>
	public static Task CopyAsync(
		string source,
		string destination,
		bool replace,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(source);
		ArgumentException.ThrowIfNullOrWhiteSpace(destination);

		var sourceRoot = NormalizeRoot(source);
		var destinationRoot = NormalizeRoot(destination);
		if (string.Equals(
				sourceRoot,
				destinationRoot,
				OperatingSystem.IsWindows()
					? StringComparison.OrdinalIgnoreCase
					: StringComparison.Ordinal))
		{
			throw new ArgumentException(
				"Source and destination data roots must be different.",
				nameof(destination));
		}

		if (!Directory.Exists(sourceRoot))
		{
			throw new DirectoryNotFoundException(
				$"Source data root does not exist: {sourceRoot}");
		}

		cancellationToken.ThrowIfCancellationRequested();
		using var leases = LeasePair.Acquire(sourceRoot, destinationRoot);
		ValidateDestination(destinationRoot, replace);

		var stagingRoot = destinationRoot + ".staging-" + Guid.NewGuid().ToString("N");
		string? backupRoot = null;
		try
		{
			Directory.CreateDirectory(stagingRoot);
			AppPaths stagingPaths = new(stagingRoot);
			DataRootHousekeeping.Prepare(stagingPaths);
			CopyDirectory(
				new AppPaths(sourceRoot).SettingsDirectory,
				stagingPaths.SettingsDirectory,
				cancellationToken);
			cancellationToken.ThrowIfCancellationRequested();
			if (Directory.Exists(destinationRoot))
			{
				if (replace)
				{
					backupRoot = destinationRoot + ".backup-" + Guid.NewGuid().ToString("N");
					Directory.Move(destinationRoot, backupRoot);
				}
				else
				{
					Directory.Delete(destinationRoot, recursive: false);
				}
			}

			Directory.Move(stagingRoot, destinationRoot);
			if (backupRoot is not null)
			{
				Directory.Delete(backupRoot, recursive: true);
				backupRoot = null;
			}

			return Task.CompletedTask;
		}
		catch
		{
			RestoreBackup(destinationRoot, backupRoot);
			DeleteDirectoryIfPresent(stagingRoot);
			throw;
		}
	}

	private static string NormalizeRoot(string path) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

	private static void ValidateDestination(string destinationRoot, bool replace)
	{
		if (!Directory.Exists(destinationRoot) || replace)
		{
			return;
		}

		if (Directory.EnumerateFileSystemEntries(destinationRoot).Any())
		{
			throw new IOException(
				$"Destination data root is not empty: {destinationRoot}. Use --replace to overwrite it.");
		}
	}

	private static void CopyDirectory(
		string sourceDirectory,
		string destinationDirectory,
		CancellationToken cancellationToken)
	{
		if (!Directory.Exists(sourceDirectory))
		{
			return;
		}

		foreach (var sourceFile in Directory.EnumerateFiles(
					 sourceDirectory,
					 "*",
					 SearchOption.AllDirectories))
		{
			cancellationToken.ThrowIfCancellationRequested();
			var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
			var destinationFile = Path.Combine(destinationDirectory, relativePath);
			Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
			File.Copy(sourceFile, destinationFile, overwrite: false);
		}
	}

	private static void RestoreBackup(string destinationRoot, string? backupRoot)
	{
		if (backupRoot is null || !Directory.Exists(backupRoot))
		{
			return;
		}

		DeleteDirectoryIfPresent(destinationRoot);
		Directory.Move(backupRoot, destinationRoot);
	}

	private static void DeleteDirectoryIfPresent(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
	}

	private sealed class LeasePair : IDisposable
	{
		private readonly AppDataProcessLease _first;
		private readonly AppDataProcessLease _second;

		private LeasePair(AppDataProcessLease first, AppDataProcessLease second)
		{
			_first = first;
			_second = second;
		}

		public static LeasePair Acquire(string sourceRoot, string destinationRoot)
		{
			(string Root, string Name)[] roots =
			[
				(sourceRoot, AppDataProcessLease.GetMutexName(sourceRoot)),
				(destinationRoot, AppDataProcessLease.GetMutexName(destinationRoot))
			];
			Array.Sort(
				roots,
				static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));

			if (!AppDataProcessLease.TryAcquire(roots[0].Root, out var first))
			{
				throw new IOException($"Data root is in use: {roots[0].Root}");
			}

			if (!AppDataProcessLease.TryAcquire(roots[1].Root, out var second))
			{
				first!.Dispose();
				throw new IOException($"Data root is in use: {roots[1].Root}");
			}

			return new LeasePair(first!, second!);
		}

		public void Dispose()
		{
			_second.Dispose();
			_first.Dispose();
		}
	}
}
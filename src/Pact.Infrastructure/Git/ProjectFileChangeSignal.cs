namespace Pact.Infrastructure.Git;

/// <summary>
/// Calls back when a file under a project root changes, as an early hint that the change list
/// may be stale. Watcher errors and dropped events are tolerated: the change list is also
/// reconciled on a timer.
/// </summary>
public sealed class ProjectFileChangeSignal : IDisposable
{
	private readonly string _root;
	private readonly Action _changed;
	private readonly FileSystemWatcher? _watcher;

	/// <summary>Starts watching <paramref name="projectRoot"/> and its subdirectories.</summary>
	public ProjectFileChangeSignal(string projectRoot, Action changed)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		_root = Path.GetFullPath(projectRoot);
		_changed = changed ?? throw new ArgumentNullException(nameof(changed));
		if (!Directory.Exists(_root))
		{
			return;
		}

		FileSystemWatcher watcher = new(_root)
		{
			IncludeSubdirectories = true,
			NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
				| NotifyFilters.LastWrite | NotifyFilters.Size
		};
		watcher.Changed += OnEvent;
		watcher.Created += OnEvent;
		watcher.Deleted += OnEvent;
		watcher.Renamed += OnEvent;
		try
		{
			watcher.EnableRaisingEvents = true;
			_watcher = watcher;
		}
		catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
		{
			watcher.Dispose();
		}
	}

	/// <summary>
	/// Whether a change to a project-relative path can affect the change list: anything outside
	/// <c>.git</c>, plus <c>.git/HEAD</c> and <c>.git/index</c>.
	/// </summary>
	public static bool IsRelevant(string relativePath)
	{
		ArgumentNullException.ThrowIfNull(relativePath);
		var path = relativePath.Replace('\\', '/');
		if (!path.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		return string.Equals(path, ".git/HEAD", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(path, ".git/index", StringComparison.OrdinalIgnoreCase);
	}

	/// <inheritdoc />
	public void Dispose() => _watcher?.Dispose();

	private void OnEvent(object sender, FileSystemEventArgs e)
	{
		if (IsRelevant(Path.GetRelativePath(_root, e.FullPath)))
		{
			_changed();
		}
	}
}

using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Pact.Core.Platform;

namespace Pact.Infrastructure.Platform;

/// <summary>App-wide, in-memory installed editor discovery and structured native process launch.</summary>
public sealed class WindowsInstalledIdeService : IInstalledIdeService, IDisposable
{
	private readonly Func<CancellationToken, IReadOnlyList<InstalledIde>> _discover;
	private readonly Func<string, byte[]?> _icon;
	private readonly Action<ProcessStartInfo> _start;
	private readonly SemaphoreSlim _discoveryGate = new(1, 1);
	private IReadOnlyList<InstalledIde>? _cached;

	/// <summary>Uses bounded Windows installation probes and the application's PATH resolver.</summary>
	public WindowsInstalledIdeService(IExecutableLocator executableLocator)
		: this(CreateDiscovery(executableLocator), WindowsExecutableIcon.ReadPng, StartProcess)
	{
	}

	internal WindowsInstalledIdeService(Func<CancellationToken, IReadOnlyList<InstalledIde>> discover,
		Func<string, byte[]?> icon, Action<ProcessStartInfo> start)
	{
		_discover = discover;
		_icon = icon;
		_start = start;
	}

	/// <inheritdoc/>
	public async Task<IReadOnlyList<InstalledIde>> DiscoverAsync(bool refresh, CancellationToken cancellationToken)
	{
		await _discoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			if (!refresh && _cached is not null)
			{
				return _cached;
			}

			var discovered = await Task.Run(() =>
			{
				var entries = _discover(cancellationToken);
				return entries.Select(entry =>
				{
					cancellationToken.ThrowIfCancellationRequested();
					try
					{
						return entry with { IconPng = _icon(entry.ExecutablePath) };
					}
					catch (Exception ex) when (ex is Win32Exception or ExternalException or IOException or UnauthorizedAccessException)
					{
						Debug.WriteLine($"IDE icon unavailable for '{entry.ExecutablePath}': {ex.Message}");
						return entry;
					}
				}).ToArray();
			}, cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			_cached = Array.AsReadOnly(discovered);
			return _cached;
		}
		finally
		{
			_discoveryGate.Release();
		}
	}

	/// <inheritdoc/>
	public Task OpenAsync(InstalledIde ide, string projectRoot, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(ide);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		return Task.Run(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			var root = Path.GetFullPath(projectRoot);
			if (!Directory.Exists(root))
			{
				throw new DirectoryNotFoundException($"Project root does not exist: {root}");
			}

			var startInfo = new ProcessStartInfo(ide.ExecutablePath)
			{
				UseShellExecute = false,
				WorkingDirectory = root
			};
			var target = root;
			if (Path.GetFileName(ide.ExecutablePath).Equals("devenv.exe", StringComparison.OrdinalIgnoreCase))
			{
				var solutions = Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
					.Where(path => Path.GetExtension(path).Equals(".sln", StringComparison.OrdinalIgnoreCase)
						|| Path.GetExtension(path).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
					.Take(2).ToArray();
				if (solutions.Length == 1)
				{
					target = solutions[0];
				}
			}
			startInfo.ArgumentList.Add(target);
			cancellationToken.ThrowIfCancellationRequested();
			_start(startInfo);
		}, cancellationToken);
	}

	/// <summary>Releases the discovery synchronization gate after callers have stopped.</summary>
	public void Dispose() => _discoveryGate.Dispose();

	private static Func<CancellationToken, IReadOnlyList<InstalledIde>> CreateDiscovery(IExecutableLocator locator)
	{
		ArgumentNullException.ThrowIfNull(locator);
		var discovery = new WindowsIdeDiscovery(locator,
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
			WindowsIdeRegistry.ReadLocations, Directory.GetDirectories);
		return discovery.Discover;
	}

	private static void StartProcess(ProcessStartInfo info)
	{
		using var process = Process.Start(info)
			?? throw new InvalidOperationException("The editor process could not be started.");
	}
}

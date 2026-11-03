using Pact.Core.Platform;
using System.Diagnostics;

namespace Pact.Infrastructure.Platform;

internal sealed record RegistryIdeLocation(string ProductId, string Path);

internal sealed class WindowsIdeDiscovery
{
	internal sealed record Product(string Id, string Name, string[] Executables, string[] Commands, string DirectoryName);

	internal static readonly Product[] Products =
	[
		new("code", "Visual Studio Code", ["Code.exe"], ["code"], "Microsoft VS Code"),
		new("visual-studio", "Visual Studio", ["devenv.exe"], ["devenv.exe"], "Microsoft Visual Studio"),
		new("rider", "Rider", ["rider64.exe", "rider.exe"], ["rider64.exe", "rider"], "Rider"),
		new("cursor", "Cursor", ["Cursor.exe"], ["cursor"], "Cursor"),
		new("windsurf", "Windsurf", ["Windsurf.exe"], ["windsurf"], "Windsurf"),
		new("zed", "Zed", ["Zed.exe"], ["zed"], "Zed"),
		new("idea", "IntelliJ IDEA", ["idea64.exe", "idea.exe"], ["idea64.exe", "idea"], "IntelliJ IDEA"),
		new("webstorm", "WebStorm", ["webstorm64.exe", "webstorm.exe"], ["webstorm64.exe", "webstorm"], "WebStorm"),
		new("pycharm", "PyCharm", ["pycharm64.exe", "pycharm.exe"], ["pycharm64.exe", "pycharm"], "PyCharm")
	];

	private readonly IExecutableLocator _locator;
	private readonly string[] _roots;
	private readonly string _localAppData;
	private readonly Func<IReadOnlyList<RegistryIdeLocation>> _registryLocations;
	private readonly Func<string, string[]> _directories;

	internal WindowsIdeDiscovery(IExecutableLocator locator, string localAppData, string programFiles,
		string programFilesX86, Func<IReadOnlyList<RegistryIdeLocation>> registryLocations,
		Func<string, string[]> directories)
	{
		_locator = locator;
		_localAppData = localAppData;
		_roots = [Path.Combine(localAppData, "Programs"), localAppData, programFiles, programFilesX86];
		_registryLocations = registryLocations;
		_directories = directories;
	}

	internal IReadOnlyList<InstalledIde> Discover(CancellationToken cancellationToken)
	{
		var found = new Dictionary<string, InstalledIde>(StringComparer.OrdinalIgnoreCase);
		foreach (var product in Products)
		{
			cancellationToken.ThrowIfCancellationRequested();
			foreach (var root in _roots.Where(root => !string.IsNullOrWhiteSpace(root)))
			{
				ProbeInstallation(product, Path.Combine(root, product.DirectoryName), found);
			}
			foreach (var command in product.Commands)
			{
				var path = _locator.FindOnPath(command);
				if (path is not null)
				{
					ProbeInstallation(product, path, found);
					// VS Code-family PATH commands are scripts in <installation>/bin.
					var directory = Path.GetDirectoryName(path);
					if (directory is not null)
					{
						ProbeInstallation(product, directory, found);
						ProbeInstallation(product, Path.GetDirectoryName(directory) ?? directory, found);
					}
				}
			}
		}

		foreach (var location in _registryLocations())
		{
			cancellationToken.ThrowIfCancellationRequested();
			var product = Array.Find(Products, item => item.Id == location.ProductId);
			if (product is not null)
			{
				ProbeInstallation(product, location.Path, found);
			}
		}

		foreach (var root in _roots)
		{
			foreach (var install in GetDirectories(root))
			{
				if (Products.Any(product => product.Id is "rider" or "idea" or "webstorm" or "pycharm"
					&& Path.GetFileName(install).StartsWith(product.DirectoryName, StringComparison.OrdinalIgnoreCase)))
				{
					ProbeJetBrains(install, found);
				}
			}
			foreach (var install in GetDirectories(Path.Combine(root, "JetBrains")))
			{
				ProbeJetBrains(install, found);
			}
			// Visual Studio layout is <year>/<edition>/Common7/IDE/devenv.exe.
			foreach (var year in GetDirectories(Path.Combine(root, "Microsoft Visual Studio")))
			{
				foreach (var edition in GetDirectories(year))
				{
					cancellationToken.ThrowIfCancellationRequested();
					ProbeInstallation(Products[1], edition, found);
				}
			}
		}

		// Covers current Toolbox product/bin and older product/channel/version/bin layouts.
		var pending = new Queue<(string Path, int Depth)>();
		pending.Enqueue((Path.Combine(_localAppData, "JetBrains", "Toolbox", "apps"), 0));
		while (pending.TryDequeue(out var item))
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (ProbeJetBrains(item.Path, found))
			{
				continue;
			}
			if (item.Depth < 3)
			{
				foreach (var child in GetDirectories(item.Path))
				{
					if (!Path.GetFileName(child).Equals("bin", StringComparison.OrdinalIgnoreCase))
					{
						pending.Enqueue((child, item.Depth + 1));
					}
				}
			}
		}

		return found.Values.GroupBy(ide => ide.Name, StringComparer.OrdinalIgnoreCase)
			.SelectMany(group => group.Count() == 1 ? group
				: group.Select(ide => ide with { Name = $"{ide.Name} ({Path.GetDirectoryName(ide.ExecutablePath)})" }))
			.OrderBy(ide => ide.Name, StringComparer.OrdinalIgnoreCase).ToArray();
	}

	private static bool ProbeJetBrains(string root, Dictionary<string, InstalledIde> found)
	{
		var detected = false;
		foreach (var product in Products.Where(product => product.Id is "rider" or "idea" or "webstorm" or "pycharm"))
		{
			detected |= ProbeInstallation(product, root, found);
		}
		return detected;
	}

	private static bool ProbeInstallation(Product product, string location, Dictionary<string, InstalledIde> found)
	{
		try
		{
			if (!Path.IsPathFullyQualified(location))
			{
				return false;
			}
			var candidates = new List<string> { location };
			foreach (var executable in product.Executables)
			{
				candidates.Add(Path.Combine(location, executable));
				candidates.Add(Path.Combine(location, "bin", executable));
				if (product.Id == "visual-studio")
				{
					candidates.Add(Path.Combine(location, "Common7", "IDE", executable));
				}
			}
			var detected = false;
			foreach (var path in candidates)
			{
				if (product.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) && File.Exists(path))
				{
					var fullPath = Path.GetFullPath(path);
					found.TryAdd(fullPath, new InstalledIde(product.Id + ":" + fullPath.ToUpperInvariant(), product.Name, fullPath, null));
					detected = true;
				}
			}
			return detected;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
		{
			Debug.WriteLine($"IDE installation probe skipped '{location}': {ex.Message}");
			return false;
		}
	}

	private string[] GetDirectories(string path)
	{
		try
		{
			return _directories(path);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
		{
			Debug.WriteLine($"IDE directory probe skipped '{path}': {ex.Message}");
			return [];
		}
	}
}

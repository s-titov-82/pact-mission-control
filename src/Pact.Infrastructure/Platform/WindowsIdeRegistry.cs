using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Pact.Infrastructure.Platform;

internal static class WindowsIdeRegistry
{
	internal static IReadOnlyList<RegistryIdeLocation> ReadLocations()
	{
		if (!OperatingSystem.IsWindows())
		{
			return [];
		}
		var result = new List<RegistryIdeLocation>();
		foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
		{
			foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
			{
				ReadHive(hive, view, result);
			}
		}
		return result;
	}

	[SupportedOSPlatform("windows")]
	private static void ReadHive(RegistryHive hive, RegistryView view, List<RegistryIdeLocation> result)
	{
		try
		{
			using var root = RegistryKey.OpenBaseKey(hive, view);
			result.AddRange(ReadHive((path, name) =>
			{
				using var key = root.OpenSubKey(path);
				return key?.GetValue(name) as string;
			}, path =>
			{
				using var key = root.OpenSubKey(path);
				return key?.GetValueNames() ?? [];
			}, path =>
			{
				using var key = root.OpenSubKey(path);
				return key?.GetSubKeyNames() ?? [];
			}));
		}
		catch (Exception ex) when (IsUnavailable(ex))
		{
			// The other hive/view and directory probes still run.
		}
	}

	// Delegates keep the real registry read order/error handling testable without registry writes.
	internal static IReadOnlyList<RegistryIdeLocation> ReadHive(
		Func<string, string?, string?> readValue,
		Func<string, string[]> getValueNames,
		Func<string, string[]> getSubKeyNames)
	{
		var result = new List<RegistryIdeLocation>();
		try
		{
			foreach (var product in WindowsIdeDiscovery.Products)
			{
				foreach (var executable in product.Executables)
				{
					try
					{
						Add(result, product.Id, readValue(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + executable, null));
					}
					catch (Exception ex) when (IsUnavailable(ex))
					{
						// Continue with later executables and registry groups in this view.
					}
				}
			}
			const string sxs = @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7";
			try
			{
				foreach (var name in getValueNames(sxs))
				{
					try
					{
						Add(result, "visual-studio", readValue(sxs, name));
					}
					catch (Exception ex) when (IsUnavailable(ex))
					{
						// An unavailable value does not hide later Visual Studio versions.
					}
				}
			}
			catch (Exception ex) when (IsUnavailable(ex))
			{
				// Uninstall registrations remain available if the SxS group is unreadable.
			}
			const string uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
			foreach (var name in getSubKeyNames(uninstall))
			{
				try
				{
					var entry = uninstall + @"\" + name;
					var displayName = readValue(entry, "DisplayName") ?? string.Empty;
					var product = Array.Find(WindowsIdeDiscovery.Products, product =>
						displayName.Contains(product.Name, StringComparison.OrdinalIgnoreCase)
						|| product.Id == "code" && displayName.Contains("Microsoft Visual Studio Code", StringComparison.OrdinalIgnoreCase));
					if (product is not null)
					{
						Add(result, product.Id, readValue(entry, "InstallLocation"));
						Add(result, product.Id, readValue(entry, "DisplayIcon"));
					}
				}
				catch (Exception ex) when (IsUnavailable(ex))
				{
					// An unreadable registration does not hide the remaining products.
				}
			}
		}
		catch (Exception ex) when (IsUnavailable(ex))
		{
			// An unreadable uninstall group retains the earlier App Paths and SxS results.
		}
		return result;
	}

	private static void Add(List<RegistryIdeLocation> result, string id, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return;
		}
		var path = Environment.ExpandEnvironmentVariables(value.Trim());
		if (path.StartsWith('"'))
		{
			var end = path.IndexOf('"', 1);
			if (end > 0)
			{
				path = path[1..end];
			}
		}
		else if (path.LastIndexOf(',') is var comma && comma >= 0 && int.TryParse(path.AsSpan(comma + 1), out _))
		{
			path = path[..comma];
		}
		result.Add(new RegistryIdeLocation(id, path));
	}

	private static bool IsUnavailable(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
}

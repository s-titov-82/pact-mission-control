using System.Security;
using Pact.Infrastructure.Platform;

namespace Pact.Infrastructure.Tests.Platform;

public sealed class WindowsIdeRegistryTests
{
	private const string AppPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
	private const string Sxs = @"SOFTWARE\Microsoft\VisualStudio\SxS\VS7";
	private const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

	[TestCase("access")]
	[TestCase("io")]
	[TestCase("security")]
	public void Inaccessible_app_path_keeps_later_product_sxs_and_uninstall_entries(string failure)
	{
		var result = WindowsIdeRegistry.ReadHive((path, name) =>
		{
			if (path == AppPaths + "Code.exe")
			{
				throw Unavailable(failure);
			}
			return ReadValue(path, name);
		}, ValueNames, SubKeyNames);

		result.ShouldBe(new RegistryIdeLocation[]
		{
			new("cursor", @"C:\Cursor\Cursor.exe"),
			new("visual-studio", @"D:\VS"),
			new("windsurf", @"D:\Windsurf")
		});
	}

	[TestCase("access")]
	[TestCase("io")]
	[TestCase("security")]
	public void Inaccessible_sxs_group_keeps_app_paths_and_uninstall_entries(string failure)
	{
		var result = WindowsIdeRegistry.ReadHive(ReadValue, path =>
		{
			path.ShouldBe(Sxs);
			throw Unavailable(failure);
		}, SubKeyNames);

		result.ShouldBe(new RegistryIdeLocation[]
		{
			new("cursor", @"C:\Cursor\Cursor.exe"),
			new("windsurf", @"D:\Windsurf")
		});
	}

	[Test]
	public void Inaccessible_sxs_value_keeps_later_value_and_uninstall_entries()
	{
		var result = WindowsIdeRegistry.ReadHive((path, name) =>
		{
			if (path == Sxs && name == "17.0")
			{
				throw new UnauthorizedAccessException();
			}
			return ReadValue(path, name);
		}, path =>
		{
			path.ShouldBe(Sxs);
			return ["17.0", "18.0"];
		}, SubKeyNames);

		result.ShouldBe(new RegistryIdeLocation[]
		{
			new("cursor", @"C:\Cursor\Cursor.exe"),
			new("visual-studio", @"D:\VS"),
			new("windsurf", @"D:\Windsurf")
		});
	}

	[Test]
	public void Inaccessible_uninstall_group_keeps_app_paths_and_sxs_entries()
	{
		var result = WindowsIdeRegistry.ReadHive(ReadValue, ValueNames, path =>
		{
			path.ShouldBe(Uninstall);
			throw new UnauthorizedAccessException();
		});

		result.ShouldBe(new RegistryIdeLocation[]
		{
			new("cursor", @"C:\Cursor\Cursor.exe"),
			new("visual-studio", @"D:\VS")
		});
	}

	[Test]
	public void Inaccessible_uninstall_entry_keeps_following_registration()
	{
		var result = WindowsIdeRegistry.ReadHive((path, name) =>
		{
			if (path == Uninstall + @"\Blocked")
			{
				throw new SecurityException();
			}
			return ReadValue(path, name);
		}, ValueNames, path =>
		{
			path.ShouldBe(Uninstall);
			return ["Blocked", "Windsurf"];
		});

		result.ShouldContain(new RegistryIdeLocation("windsurf", @"D:\Windsurf"));
	}

	private static string? ReadValue(string path, string? name) => (path, name) switch
	{
		(AppPaths + "Cursor.exe", null) => @"C:\Cursor\Cursor.exe",
		(Sxs, "18.0") => @"D:\VS",
		(Uninstall + @"\Windsurf", "DisplayName") => "Windsurf",
		(Uninstall + @"\Windsurf", "InstallLocation") => @"D:\Windsurf",
		_ => null
	};

	private static string[] ValueNames(string path)
	{
		path.ShouldBe(Sxs);
		return ["18.0"];
	}

	private static string[] SubKeyNames(string path)
	{
		path.ShouldBe(Uninstall);
		return ["Windsurf"];
	}

	private static Exception Unavailable(string failure) => failure switch
	{
		"access" => new UnauthorizedAccessException(),
		"io" => new IOException(),
		"security" => new SecurityException(),
		_ => throw new ArgumentException("Unknown failure", nameof(failure))
	};
}

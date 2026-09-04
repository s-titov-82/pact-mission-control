using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Pact.Infrastructure.Terminal;

/// <summary>
/// Resolves the logical library name "conpty" to the bundled modern
/// conpty.dll (shipped with OpenConsole.exe under the application's
/// conpty\ folder). The inbox Windows ConPTY does not translate a client's
/// ENABLE_MOUSE_INPUT into VT mouse sequences; the bundled one does, which
/// is required for mouse scroll in crossterm-based TUIs such as codex.
/// There is deliberately no fallback to kernel32: a missing DLL is a
/// packaging error and must fail loudly.
/// </summary>
public static class ConptyLibrary
{
	/// <summary>
	/// Logical name used on the <c>Conpty*</c> P/Invoke declarations and mapped by the resolver
	/// to the bundled DLL.
	/// </summary>
	public const string LibraryName = "conpty";

	private static bool Registered;

	/// <summary>
	/// Returns the expected path of the bundled <c>conpty.dll</c> beneath
	/// <paramref name="baseDirectory"/>. The file's presence is not checked here; loading fails
	/// loudly if it is missing.
	/// </summary>
	public static string ResolveDllPath(string baseDirectory)
	{
		return Path.Combine(baseDirectory, "conpty", "conpty.dll");
	}

	internal static void EnsureBundleAvailable(string baseDirectory)
	{
		var missingFiles = new List<string>();
		if (!File.Exists(ResolveDllPath(baseDirectory)))
		{
			missingFiles.Add("conpty.dll");
		}

		if (!File.Exists(Path.Combine(baseDirectory, "conpty", "OpenConsole.exe")))
		{
			missingFiles.Add("OpenConsole.exe");
		}

		if (missingFiles.Count > 0)
		{
			throw new InvalidOperationException(
				$"Bundled ConPTY files are missing ({string.Join(", ", missingFiles)}). "
				+ "The application package is incomplete — rebuild or reinstall.");
		}
	}

#pragma warning disable CA2255 // Intentional: registers the DllImportResolver as soon as this assembly loads.
	[ModuleInitializer]
	internal static void Register()
	{
		if (Registered)
		{
			return;
		}

		Registered = true;
		NativeLibrary.SetDllImportResolver(typeof(ConptyLibrary).Assembly, Resolve);
	}
#pragma warning restore CA2255

	private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
	{
		if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal))
		{
			return IntPtr.Zero;
		}

		EnsureBundleAvailable(AppContext.BaseDirectory);
		var dllPath = ResolveDllPath(AppContext.BaseDirectory);

		if (!NativeLibrary.TryLoad(dllPath, out var handle))
		{
			throw new InvalidOperationException($"Failed to load bundled ConPTY library '{dllPath}'.");
		}

		return handle;
	}
}

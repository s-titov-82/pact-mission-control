namespace Pact.App.Avalonia.Tests.Platform;

public sealed class ConptyAssetTests
{
	[Test]
	public void Bundled_conpty_binaries_are_copied_to_output()
	{
		var conptyDirectory = Path.Combine(AppContext.BaseDirectory, "conpty");

		File.Exists(Path.Combine(conptyDirectory, "conpty.dll")).ShouldBeTrue($"conpty.dll missing from {conptyDirectory}");
		File.Exists(Path.Combine(conptyDirectory, "OpenConsole.exe")).ShouldBeTrue($"OpenConsole.exe missing from {conptyDirectory}");
	}
}
using Pact.Infrastructure.Terminal;

namespace Pact.Infrastructure.Tests.Terminal;

public sealed class ConptyLibraryTests
{
	[Test]
	public void ResolveDllPath_points_into_conpty_subdirectory()
	{
		var path = ConptyLibrary.ResolveDllPath(@"C:\app\bin");

		path.ShouldBe(@"C:\app\bin\conpty\conpty.dll");
	}

	[Test]
	public void Bundled_files_are_available_in_test_output()
	{
		Should.NotThrow(() => ConptyLibrary.EnsureBundleAvailable(AppContext.BaseDirectory));
	}

	[Test]
	public void Missing_bundle_reports_an_actionable_packaging_error()
	{
		var missingDirectory = Path.Combine(
			Path.GetTempPath(),
			$"Pact-missing-conpty-{Guid.NewGuid():N}");

		var exception = Should.Throw<InvalidOperationException>(
			() => ConptyLibrary.EnsureBundleAvailable(missingDirectory));

		exception.Message.ShouldContain("application package is incomplete");
		exception.Message.ShouldContain("conpty.dll");
		exception.Message.ShouldContain("OpenConsole.exe");
	}
}
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class AppDataProfileResolverTests
{
	[Test]
	public void Resolve_WithoutOverride_UsesRequestedAppDataDirectory()
	{
		var result = AppDataProfileResolver.Resolve(
			[], "Pact_Avalonia", "avalonia-preview");

		result.Name.ShouldBe("avalonia-preview");
		result.RootDirectory.ShouldBe(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"Pact_Avalonia"));
	}

	[Test]
	public void Resolve_DataRootOverride_NormalizesAbsolutePath()
	{
		var requested = Path.Combine(Path.GetTempPath(), "agent-terminal", "..", "preview");

		var result = AppDataProfileResolver.Resolve(
			["--data-root", requested], "Pact_Avalonia", "avalonia-preview");

		result.RootDirectory.ShouldBe(Path.GetFullPath(requested));
	}

	[Test]
	[TestCase("--data-root")]
	[TestCase("--data-root=")]
	public void Resolve_MissingDataRootValue_Throws(string argument) => Should.Throw<ArgumentException>(() =>
																				 AppDataProfileResolver.Resolve([argument], "Pact", "stable-wpf"));

	[Test]
	public void Resolve_RelativeDataRoot_Throws() => Should.Throw<ArgumentException>(() =>
															  AppDataProfileResolver.Resolve(
																  ["--data-root", "relative-preview-root"],
																  "Pact_Avalonia",
																  "avalonia-preview"));
}
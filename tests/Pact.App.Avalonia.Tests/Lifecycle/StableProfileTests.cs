using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Lifecycle;

public sealed class StableProfileTests
{
	[Test]
	public void NoArgumentStartupUsesStableAvaloniaDefaultsAndProductTitles()
	{
		var profile = AppProfileDefaults.Resolve([]);

		profile.Name.ShouldBe("stable-avalonia");
		profile.RootDirectory.ShouldBe(Path.GetFullPath(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
				"Pact")));
		AppProfileDefaults.ProductTitle.ShouldBe("PACT:> Mission Control");
		AppProfileDefaults.ReadyWindowTitle.ShouldBe("PACT:> Mission Control");
		AppProfileDefaults.StartupFailedWindowTitle("broken")
			.ShouldBe("PACT:> Mission Control - Startup failed: broken");
		AppProfileDefaults.DataRootInUseMessage(profile).Contains("Preview", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
	}

	[Test]
	public void AbsoluteOverrideIsPreservedAndUsesTheSameRootDerivedLease()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var profile = AppProfileDefaults.Resolve(["--data-root", root]);

		profile.RootDirectory.ShouldBe(root);
		profile.Name.ShouldBe("stable-avalonia");
		AppDataProcessLease.TryAcquire(profile.RootDirectory, out var first).ShouldBeTrue();
		try
		{
			AppDataProcessLease.TryAcquire(profile.RootDirectory, out var second).ShouldBeFalse();
			try
			{
				second.ShouldBeNull();
			}
			finally
			{
				second?.Dispose();
			}
		}
		finally
		{
			first!.Dispose();
		}
	}
}

using Pact.Infrastructure.Orchestrator;

namespace Pact.Infrastructure.Tests.Orchestrator;

public sealed class HermesHomeTests
{
	private static readonly string NativeRoot = OperatingSystem.IsWindows()
		? @"C:\Profiles\dev\AppData\Local\hermes"
		: "/home/dev/.hermes";

	[Test]
	public void Unset_home_resolves_to_the_native_root()
	{
		HermesHome.ResolveRoot(null, NativeRoot).ShouldBe(NativeRoot);
		HermesHome.ResolveRoot("   ", NativeRoot).ShouldBe(NativeRoot);
	}

	[Test]
	public void Home_inside_the_native_root_still_resolves_to_that_root()
	{
		HermesHome.ResolveRoot(NativeRoot, NativeRoot).ShouldBe(NativeRoot);
		HermesHome.ResolveRoot(
				Path.Combine(NativeRoot, "profiles", "pact"),
				NativeRoot)
			.ShouldBe(NativeRoot);
	}

	[Test]
	public void Home_outside_the_native_root_replaces_it()
	{
		var relocated = OperatingSystem.IsWindows() ? @"D:\hermes-data" : "/opt/hermes-data";

		HermesHome.ResolveRoot(relocated, NativeRoot).ShouldBe(relocated);
	}

	[Test]
	public void Relocated_profile_home_resolves_to_the_root_that_owns_it()
	{
		var relocatedRoot = OperatingSystem.IsWindows() ? @"D:\hermes-data" : "/opt/hermes-data";

		HermesHome.ResolveRoot(
				Path.Combine(relocatedRoot, "profiles", "pact"),
				NativeRoot)
			.ShouldBe(relocatedRoot);
	}

	[Test]
	public void Trailing_separators_do_not_change_the_resolved_root()
	{
		HermesHome.ResolveRoot(NativeRoot + Path.DirectorySeparatorChar, NativeRoot)
			.ShouldBe(NativeRoot);
	}

	[Test]
	public void Windows_defaults_to_local_application_data_rather_than_the_home_directory()
	{
		var root = HermesHome.PlatformDefaultRoot();

		if (OperatingSystem.IsWindows())
		{
			root.ShouldBe(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"hermes"));
			root.ShouldNotBe(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".hermes"));
		}
		else
		{
			root.ShouldBe(Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".hermes"));
		}
	}

	[Test]
	public void Profile_directory_sits_under_the_profiles_folder()
	{
		HermesHome.ProfileDirectory(NativeRoot, "pact")
			.ShouldBe(Path.Combine(NativeRoot, "profiles", "pact"));
	}
}

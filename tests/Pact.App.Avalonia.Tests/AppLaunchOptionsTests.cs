namespace Pact.App.Avalonia.Tests;

public sealed class AppLaunchOptionsTests
{
	private const string RestartId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Test]
	public void Parse_accepts_complete_typed_option_set_and_normalizes_paths()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var engineOutput = Path.Combine(root, "Temp", "engine.json");
		var restartOutput = Path.Combine(root, "Temp", "restart.json");

		var options = AppLaunchOptions.Parse([
			$"--data-root={root}",
			"--engine-probe-output", engineOutput,
			"--soft-restart-id", RestartId,
			"--soft-restart-probe-output", restartOutput
		]);

		options.Profile.RootDirectory.ShouldBe(Path.GetFullPath(root));
		options.PassDataRoot.ShouldBeTrue();
		options.EngineProbeOutputPath.ShouldBe(Path.GetFullPath(engineOutput));
		options.SoftRestartId.ShouldBe(RestartId);
		options.SoftRestartProbeOutputPath.ShouldBe(Path.GetFullPath(restartOutput));
	}

	[Test]
	public void Parse_supports_separate_data_root_and_tracks_default_root_replay()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var explicitOptions = AppLaunchOptions.Parse([
			"--data-root", temporaryDirectory.Path
		]);
		var defaults = AppLaunchOptions.Parse([]);

		explicitOptions.PassDataRoot.ShouldBeTrue();
		defaults.PassDataRoot.ShouldBeFalse();
		defaults.Profile.RootDirectory.ShouldBe(Path.GetFullPath(Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			AppProfileDefaults.DataDirectoryName)));
	}

	[TestCase("--unknown")]
	[TestCase("--data-root")]
	[TestCase("--data-root=")]
	[TestCase("--data-root|relative")]
	[TestCase("--engine-probe-output")]
	[TestCase("--engine-probe-output|relative.json")]
	[TestCase("--soft-restart-id")]
	[TestCase("--soft-restart-id|ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
	[TestCase("--soft-restart-id|short")]
	[TestCase("--soft-restart-probe-output")]
	[TestCase("--soft-restart-probe-output|relative.json")]
	public void Parse_rejects_unknown_missing_empty_relative_or_invalid_options(string encoded)
	{
		ArgumentNullException.ThrowIfNull(encoded);
		string[] args = encoded.Split('|');

		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse(args));
	}

	[Test]
	public void Parse_rejects_duplicates_and_probe_output_without_restart_id()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var first = Path.Combine(root, "Temp", "first.json");
		var second = Path.Combine(root, "Temp", "second.json");

		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--data-root", root,
			$"--data-root={root}"
		]));
		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--data-root", root,
			"--engine-probe-output", first,
			"--engine-probe-output", second
		]));
		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--soft-restart-id", RestartId,
			"--soft-restart-id", RestartId
		]));
		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--data-root", root,
			"--soft-restart-probe-output", first,
			"--soft-restart-probe-output", second
		]));
	}

	[Test]
	public void Diagnostic_probe_is_valid_on_the_initial_launch_without_a_restart_id()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var output = Path.Combine(root, "Temp", "restart.json");

		var options = AppLaunchOptions.Parse([
			"--data-root", root,
			"--soft-restart-probe-output", output
		]);

		options.SoftRestartId.ShouldBeNull();
		options.SoftRestartProbeOutputPath.ShouldBe(output);
	}

	[Test]
	public void Engine_probe_output_must_be_below_selected_data_root_temp()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var outside = Path.Combine(root, "Settings", "probe.json");

		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--data-root", root,
			"--engine-probe-output", outside
		]));
	}

	[Test]
	public void Soft_restart_probe_output_must_be_below_selected_data_root_temp()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var outside = Path.Combine(root, "Settings", "probe.json");

		Should.Throw<ArgumentException>(() => AppLaunchOptions.Parse([
			"--data-root", root,
			"--soft-restart-probe-output", outside
		]));
	}
}

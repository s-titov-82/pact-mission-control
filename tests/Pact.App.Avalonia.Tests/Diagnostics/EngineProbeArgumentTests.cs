using Pact.App.Avalonia.Diagnostics;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class EngineProbeArgumentTests
{
	[Test]
	[TestCase("navigation-completed")]
	[TestCase("javascript-ready")]
	[TestCase("webmessage-thread-sequence")]
	[TestCase("runtime-started")]
	[TestCase("first-clean-terminal-output")]
	[TestCase("browser-first-render")]
	[TestCase("terminal-browser-terminal-switch")]
	[TestCase("adapter-lifecycle")]
	[TestCase("shutdown-ui-thread")]
	[TestCase("dom-text")]
	[TestCase("dom-attribute")]
	[TestCase("dom-regex")]
	[TestCase("dom-missing")]
	[TestCase("background-timer")]
	public void RequiredProbesIncludeProductionPathEvidence(string phase) => EngineProbeRunner.RequiredProbes.ShouldContain(phase);

	[Test]
	public void TryCreate_returns_null_when_argument_is_absent()
	{
		var profile = CreateProfile();

		EngineProbeRunner.TryCreate(["--data-root", profile.RootDirectory], profile).ShouldBeNull();
	}

	[Test]
	public void TryCreate_rejects_relative_output_path()
	{
		var profile = CreateProfile();
		var exception = Should.Throw<ArgumentException>(() =>
			EngineProbeRunner.TryCreate(["--engine-probe-output", "probe.json"], profile));

		exception.Message.Contains("absolute", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public void TryCreate_rejects_duplicate_argument()
	{
		var profile = CreateProfile();
		var first = Path.Combine(profile.RootDirectory, "Temp", "first.json");
		var second = Path.Combine(profile.RootDirectory, "Temp", "second.json");

		var exception = Should.Throw<ArgumentException>(() =>
			EngineProbeRunner.TryCreate([
				"--engine-probe-output", first,
				"--engine-probe-output", second], profile));

		exception.Message.Contains("once", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public void TryCreate_requires_output_below_the_profile_temp_directory()
	{
		var profile = CreateProfile();
		var outsideTemp = Path.Combine(profile.RootDirectory, "Settings", "probe.json");

		var exception = Should.Throw<ArgumentException>(() =>
			EngineProbeRunner.TryCreate(["--engine-probe-output", outsideTemp], profile));

		exception.Message.Contains("Temp", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public void TryCreate_accepts_output_below_the_profile_temp_directory()
	{
		var profile = CreateProfile();
		var output = Path.Combine(profile.RootDirectory, "Temp", "engine-probe.json");

		EngineProbeRunner.TryCreate(["--engine-probe-output", output], profile).ShouldNotBeNull();
	}

	[Test]
	[TestCase("\u001b[1t\u001b[c\u001b[?1004h\u001b[?9001h", false)]
	[TestCase("\u001b[32mFINAL_ENGINE_PROBE_READY\u001b[0m", true)]
	public void Visible_output_wait_gate_ignores_escape_sequences(string output, bool expected) => EngineProbeRunner.HasVisibleText(output).ShouldBe(expected);

	private static AppDataProfile CreateProfile() => new(
		"test",
		Path.Combine(Path.GetTempPath(), "Pact-engine-probe-tests", Guid.NewGuid().ToString("N")));
}
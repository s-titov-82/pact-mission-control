using System.Text.Json;
using Pact.App.Avalonia.Diagnostics;

namespace Pact.App.Avalonia.Tests.Diagnostics;

public sealed class SoftRestartProbeArgumentTests
{
	private const string RestartId =
		"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

	[Test]
	public void Probe_argument_is_typed_on_initial_and_relaunched_processes()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var output = Path.Combine(temporaryDirectory.Path, "Temp", "probe.json");

		var initial = AppLaunchOptions.Parse([
			"--data-root", temporaryDirectory.Path,
			"--soft-restart-probe-output", output
		]);
		var relaunched = AppLaunchOptions.Parse([
			"--data-root", temporaryDirectory.Path,
			"--soft-restart-id", RestartId,
			"--soft-restart-probe-output", output
		]);

		initial.SoftRestartId.ShouldBeNull();
		initial.SoftRestartProbeOutputPath.ShouldBe(output);
		relaunched.SoftRestartId.ShouldBe(RestartId);
		relaunched.SoftRestartProbeOutputPath.ShouldBe(output);
	}

	[Test]
	public async Task Evidence_is_atomically_replaced_and_bounded()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var output = Path.Combine(temporaryDirectory.Path, "nested", "probe.json");
		Directory.CreateDirectory(Path.GetDirectoryName(output)!);
		await File.WriteAllTextAsync(output, "incomplete");
		var longItems = Enumerable.Range(0, 300)
			.Select(index => $"{index:D3}:{new string('x', 200)}")
			.ToArray();

		await SoftRestartProbeEvidenceWriter.WriteAsync(
			output,
			new SoftRestartProbeEvidence(
				RestartId,
				longItems,
				longItems,
				longItems,
				longItems,
				true,
				true,
				"setup-failed"),
			CancellationToken.None);

		using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output));
		foreach (var propertyName in new[]
				 {
					 "RestoredTerminalIds",
					 "ColdStartFallbacks",
					 "RestoredWebPageIds",
					 "Failures"
				 })
		{
			var values = document.RootElement.GetProperty(propertyName)
				.EnumerateArray()
				.Select(item => item.GetString()!)
				.ToArray();
			values.Length.ShouldBe(256);
			values.ShouldAllBe(value => value.Length <= 160);
		}
		Directory.EnumerateFiles(Path.GetDirectoryName(output)!)
			.ShouldBe([output]);
	}
}

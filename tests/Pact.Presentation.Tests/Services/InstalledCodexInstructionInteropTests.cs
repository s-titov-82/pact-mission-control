using System.Diagnostics;
using Pact.Core.Platform;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class InstalledCodexInstructionInteropTests
{
	[Test]
	[Explicit("Requires the installed Codex CLI; debug prompt-input performs no model call.")]
	public async Task Bare_codex_pipeline_preserves_dollar_and_backtick_in_developer_instruction()
	{
		const string instruction = "PACT literal marker: $cash and `tick";
		TerminalCommandResolver resolver = new(new MissingExecutableLocator());
		string commandLine = (await resolver.ResolveCommandLineAsync(
			"codex debug prompt-input",
			["-c", $"developer_instructions={instruction}"]))!;
		string[] commandParts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);

		ProcessStartInfo startInfo = new()
		{
			FileName = commandParts[0],
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (string argument in commandParts[1..])
		{
			startInfo.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Codex interop process did not start.");
		string output = await process.StandardOutput.ReadToEndAsync();
		string error = await process.StandardError.ReadToEndAsync();
		await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));

		process.ExitCode.ShouldBe(0, error);
		output.ShouldContain("$cash");
		output.ShouldContain("`tick");
	}

	private sealed class MissingExecutableLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName) => null;
	}
}

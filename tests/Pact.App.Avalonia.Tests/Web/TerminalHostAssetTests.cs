using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pact.App.Avalonia.Tests.Web;

public sealed partial class TerminalHostAssetTests
{
	private const int MinimumNodeMajorVersion = 22;
	private static readonly Lazy<(bool IsSupported, string Message)> NodePrerequisite =
		new(DetectNodePrerequisite);

	[Test]
	[TestCase("same-final-screen")]
	[TestCase("wrapped-pwsh-prompt")]
	[TestCase("wrapped-busy-marker")]
	[TestCase("wrapped-claude-composer")]
	[TestCase("show-terminal-options")]
	[TestCase("dynamic-churn-snapshot")]
	[TestCase("typing-produces-no-dynamic-snapshot")]
	[TestCase("right-click-owned-by-host")]
	[TestCase("terminal-link-owned-by-session")]
	[TestCase("theme-switch-updates-existing-and-new-terminals")]
	[TestCase("adaptive-output-batching")]
	[TestCase("prebatched-output")]
	[TestCase("resize-bridge")]
	[TestCase("modified-enter")]
	[TestCase("selection")]
	[TestCase("selection-completion")]
	[TestCase("selection-dismiss")]
	[TestCase("osc52")]
	[TestCase("selected-text-request")]
	public async Task Terminal_host_snapshot_behaviors_execute_in_node(string behavior)
	{
		EnsureNodePrerequisite();
		var repositoryRoot = FindRepositoryRoot();
		ProcessStartInfo startInfo = new("node")
		{
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true
		};
		startInfo.ArgumentList.Add(Path.Combine(
			repositoryRoot,
			"tests",
			"Pact.App.Avalonia.Tests",
			"Web",
			"terminalHost.behavior.test.js"));
		startInfo.ArgumentList.Add(Path.Combine(
			repositoryRoot,
			"src",
			"Pact.App.Avalonia",
			"WebAssets",
			"terminalHost.js"));
		startInfo.ArgumentList.Add(behavior);

		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Could not start Node.js for terminal host behavior tests.");
		var output = await process.StandardOutput.ReadToEndAsync(TestContext.CurrentContext.CancellationToken);
		var error = await process.StandardError.ReadToEndAsync(TestContext.CurrentContext.CancellationToken);
		await process.WaitForExitAsync(TestContext.CurrentContext.CancellationToken);

		(process.ExitCode == 0).ShouldBeTrue($"Node behavior '{behavior}' failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{error}");
	}

	[Test]
	[TestCase("v22.0.0", true)]
	[TestCase("v24.17.0", true)]
	[TestCase("v21.7.3", false)]
	[TestCase("unexpected", false)]
	[TestCase("", false)]
	public void Node_version_preflight_enforces_supported_floor(string output, bool expectedSupport)
	{
		ArgumentNullException.ThrowIfNull(output);
		(var IsSupported, var Message) = EvaluateNodeVersion(
			output,
			exitCode: 0,
			standardError: string.Empty);

		IsSupported.ShouldBe(expectedSupport);
		if (!expectedSupport)
		{
			Message.Contains("Node.js 22+", StringComparison.Ordinal).ShouldBeTrue();
			Message.Contains("rtk node --version", StringComparison.Ordinal).ShouldBeTrue();
		}
	}

	[Test]
	public void Missing_node_preflight_message_is_actionable()
	{
		(var IsSupported, var Message) = CreateNodeLaunchFailure("executable not found");

		IsSupported.ShouldBeFalse();
		Message.Contains("Node.js 22+", StringComparison.Ordinal).ShouldBeTrue();
		Message.Contains("PATH", StringComparison.Ordinal).ShouldBeTrue();
		Message.Contains("rtk node --version", StringComparison.Ordinal).ShouldBeTrue();
		Message.Contains("no npm install", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	private static void EnsureNodePrerequisite()
	{
		(var IsSupported, var Message) = NodePrerequisite.Value;
		IsSupported.ShouldBeTrue(Message);
	}

	private static (bool IsSupported, string Message) DetectNodePrerequisite()
	{
		try
		{
			ProcessStartInfo startInfo = new("node", "--version")
			{
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true
			};
			using var process = Process.Start(startInfo);
			if (process is null)
			{
				return CreateNodeLaunchFailure("the process launcher returned no process");
			}

			var output = process.StandardOutput.ReadToEnd();
			var error = process.StandardError.ReadToEnd();
			process.WaitForExit();
			return EvaluateNodeVersion(output, process.ExitCode, error);
		}
		catch (Win32Exception exception)
		{
			return CreateNodeLaunchFailure(exception.Message);
		}
	}

	private static (bool IsSupported, string Message) EvaluateNodeVersion(
		string output,
		int exitCode,
		string standardError)
	{
		if (exitCode != 0)
		{
			return CreateNodeLaunchFailure(
				$"`node --version` exited with code {exitCode}: {standardError.Trim()}");
		}

		var versionText = output.Trim();
		var match = MyRegex().Match(versionText);
		if (!match.Success
			|| !int.TryParse(match.Groups["major"].Value, out var majorVersion))
		{
			return CreateNodeLaunchFailure(
				$"`node --version` returned an unrecognized value: '{versionText}'");
		}

		if (majorVersion < MinimumNodeMajorVersion)
		{
			return CreateNodeLaunchFailure(
				$"detected {versionText}, which is below the supported test floor");
		}

		return (true, string.Empty);
	}

	private static (bool IsSupported, string Message) CreateNodeLaunchFailure(string detail) =>
		(false,
			$"Node.js {MinimumNodeMajorVersion}+ is required on PATH to run terminal host behavior tests; "
			+ $"{detail}. Verify with `rtk node --version`; no npm install is required.");

	private static string FindRepositoryRoot()
	{
		var directory = AppContext.BaseDirectory;
		while (!File.Exists(Path.Combine(directory, "Pact.slnx")))
		{
			var parent = Directory.GetParent(directory) ?? throw new FileNotFoundException("Could not locate Pact.slnx from test output directory.");

			directory = parent.FullName;
		}

		return directory;
	}

	[GeneratedRegex(@"^v(?<major>[0-9]+)(?:\.|$)", RegexOptions.CultureInvariant)]
	private static partial Regex MyRegex();
}

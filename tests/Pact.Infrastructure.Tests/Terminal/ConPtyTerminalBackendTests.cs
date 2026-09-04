using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pact.Core.Terminal;
using Pact.Infrastructure.Terminal;

namespace Pact.Infrastructure.Tests.Terminal;

public sealed class ConPtyTerminalBackendTests
{
	private const string Win32EnterKey =
		"\u001b[13;28;13;1;0;1_\u001b[13;28;13;0;0;1_";

	[Test]
	[Platform("Win")]
	[Category("NativeIntegration")]
	[NonParallelizable]
	public async Task Bundled_conpty_round_trips_console_input_and_completes_output()
	{
		var probePath = ResolveProbePath();
		var readyPath = Path.Combine(
			Path.GetTempPath(),
			$"Pact-ConPty-input-ready-{Guid.NewGuid():N}.txt");
		var resultPath = Path.Combine(
			Path.GetTempPath(),
			$"Pact-ConPty-input-result-{Guid.NewGuid():N}.txt");
		try
		{
			await using ConPtyTerminalBackend backend = new();
			using var timeout = CreateTimeout(TimeSpan.FromSeconds(15));
			var session = await backend.StartAsync(
				new TerminalStartOptions(
					$"\"{probePath}\" --input-ready \"{readyPath}\" --result \"{resultPath}\"",
					Path.GetTempPath(),
					80,
					25),
				timeout.Token);
			using var process = Process.GetProcessById(session.ProcessId.ShouldNotBeNull());
			TaskCompletionSource inputModeEnabled = new(TaskCreationOptions.RunContinuationsAsynchronously);
			var output = ReadAllOutputAsync(
				backend,
				inputModeEnabled,
				timeout.Token);
			while (!File.Exists(readyPath))
			{
				await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
			}

			await inputModeEnabled.Task.WaitAsync(timeout.Token);
			await backend.WriteAsync(
				Encoding.UTF8.GetBytes("\u001b[?1;2c\u001b[I"),
				timeout.Token);
			await backend.WriteAsync(
				Encoding.UTF8.GetBytes($"{EncodeWin32Text("pact")}{Win32EnterKey}"),
				timeout.Token);

			while (!File.Exists(resultPath))
			{
				await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
			}

			await process.WaitForExitAsync(timeout.Token);
			await backend.StopAsync(timeout.Token);
			(await output).ShouldContain("PACT_CONPTY_ECHO:pact");
			(await File.ReadAllTextAsync(resultPath, timeout.Token)).ShouldBe("pact");
		}
		finally
		{
			File.Delete(readyPath);
			File.Delete(resultPath);
		}
	}

	[Test]
	[Platform("Win")]
	[Category("NativeIntegration")]
	[NonParallelizable]
	public async Task Stop_terminates_a_long_running_pwsh_process_tree_within_the_documented_bound()
	{
		var scriptPath = Path.Combine(
			Path.GetTempPath(),
			$"Pact-ConPty-stop-{Guid.NewGuid():N}.ps1");
		var childPidPath = Path.Combine(
			Path.GetTempPath(),
			$"Pact-ConPty-child-{Guid.NewGuid():N}.txt");
		await File.WriteAllTextAsync(
			scriptPath,
			"""
			param([string]$ChildPidPath)
			$child = Start-Process pwsh.exe `
				-ArgumentList @('-NoLogo', '-NoProfile', '-Command', 'Start-Sleep -Seconds 60') `
				-NoNewWindow `
				-PassThru
			$temporaryPath = "$ChildPidPath.tmp"
			[System.IO.File]::WriteAllText($temporaryPath, [string]$child.Id)
			[System.IO.File]::Move($temporaryPath, $ChildPidPath)
			Start-Sleep -Seconds 60
			""",
			TestContext.CurrentContext.CancellationToken);

		Process? child = null;
		try
		{
			await using ConPtyTerminalBackend backend = new();
			using var timeout = CreateTimeout(TimeSpan.FromSeconds(15));
			var session = await backend.StartAsync(
				new TerminalStartOptions(
					$"pwsh.exe -NoLogo -NoProfile -File \"{scriptPath}\" -ChildPidPath \"{childPidPath}\"",
					Path.GetTempPath(),
					80,
					25),
				timeout.Token);
			var processId = session.ProcessId.ShouldNotBeNull();
			using var parent = Process.GetProcessById(processId);
			while (!File.Exists(childPidPath))
			{
				await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
			}

			var childProcessId = int.Parse(
				await File.ReadAllTextAsync(childPidPath, timeout.Token),
				CultureInfo.InvariantCulture);
			child = Process.GetProcessById(childProcessId);
			var elapsed = Stopwatch.StartNew();

			await backend.StopAsync(timeout.Token);
			await parent.WaitForExitAsync(timeout.Token);
			await child.WaitForExitAsync(timeout.Token);

			elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
		}
		finally
		{
			if (child is { HasExited: false })
			{
				child.Kill(entireProcessTree: true);
				await child.WaitForExitAsync();
			}

			child?.Dispose();
			File.Delete(scriptPath);
			File.Delete(childPidPath);
		}
	}

	[Test]
	[Platform("Win")]
	[Category("NativeIntegration")]
	[NonParallelizable]
	public async Task Bundled_conpty_applies_resize_to_the_child_console()
	{
		await using var root = TemporaryDirectory.Create();
		await using ConPtyTerminalBackend backend = new();
		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.CurrentContext.CancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(15));
		var probePath = ResolveProbePath();
		var resultPath = Path.Combine(root.Path, "result.txt");

		await backend.StartAsync(
			new TerminalStartOptions(
				$"\"{probePath}\" --delay 750 --result \"{resultPath}\"",
				Path.GetTempPath(),
				80,
				25),
			timeout.Token);

		await backend.ResizeAsync(101, 37, timeout.Token);
		while (!File.Exists(resultPath))
		{
			await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
		}

		(await File.ReadAllTextAsync(resultPath, timeout.Token))
			.ShouldBe(
				"PACT_ORIGIN=1;BOUNDARY=1;OUTSIDE_WIDTH=0;OUTSIDE_HEIGHT=0");
		await backend.StopAsync(timeout.Token);
	}

	[Test]
	public async Task Blocking_process_wait_runs_off_the_caller()
	{
		using ManualResetEventSlim entered = new();
		using ManualResetEventSlim release = new();

		var wait = ConPtyTerminalBackend.RunBlockingProcessStopAsync(() =>
		{
			entered.Set();
			release.Wait();
		});

		entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
		wait.IsCompleted.ShouldBeFalse();

		release.Set();
		await wait.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
	}

	private static CancellationTokenSource CreateTimeout(TimeSpan timeout)
	{
		var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
			TestContext.CurrentContext.CancellationToken);
		cancellation.CancelAfter(timeout);
		return cancellation;
	}

	private static async Task<string> ReadAllOutputAsync(
		ConPtyTerminalBackend backend,
		TaskCompletionSource inputModeEnabled,
		CancellationToken cancellationToken)
	{
		using MemoryStream output = new();
		await foreach (var chunk in backend.ReadOutputAsync(cancellationToken))
		{
			await output.WriteAsync(chunk, cancellationToken);
			if (!inputModeEnabled.Task.IsCompleted
				&& Encoding.UTF8.GetString(output.ToArray()).Contains("\u001b[?9001h", StringComparison.Ordinal))
			{
				inputModeEnabled.TrySetResult();
			}
		}

		return Encoding.UTF8.GetString(output.ToArray());
	}

	private static string EncodeWin32Text(string text)
	{
		StringBuilder encoded = new();
		foreach (var character in text)
		{
			(var virtualKey, var scanCode) = character switch
			{
				'p' => (80, 25),
				'a' => (65, 30),
				'c' => (67, 46),
				't' => (84, 20),
				_ => throw new ArgumentOutOfRangeException(nameof(text), character, "Unsupported test character."),
			};
			encoded.Append(CultureInfo.InvariantCulture, $"\u001b[{virtualKey};{scanCode};{(int)character};1;0;1_");
			encoded.Append(CultureInfo.InvariantCulture, $"\u001b[{virtualKey};{scanCode};{(int)character};0;0;1_");
		}

		return encoded.ToString();
	}

	private static string ResolveProbePath()
	{
		DirectoryInfo outputDirectory = new(AppContext.BaseDirectory);
		var configuration = outputDirectory.Parent?.Name
			?? throw new InvalidOperationException("Could not determine the test build configuration.");
		var directory = outputDirectory;
		while (directory is not null
			&& !Directory.Exists(Path.Combine(directory.FullName, ".git"))
			&& !File.Exists(Path.Combine(directory.FullName, ".git")))
		{
			directory = directory.Parent;
		}

		var repositoryRoot = directory?.FullName
			?? throw new InvalidOperationException("Could not locate the repository root.");
		var path = Path.Combine(
			repositoryRoot,
			"tests",
			"Pact.ConPty.TestProbe",
			"bin",
			configuration,
			"net10.0",
			"Pact.ConPty.TestProbe.exe");
		File.Exists(path).ShouldBeTrue($"expected ConPTY test probe at {path}");
		return path;
	}
}

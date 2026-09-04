using System.Collections.Concurrent;
using System.Diagnostics;

namespace Pact.Infrastructure.Tests.Git;

public sealed class GitCliRunnerTests : IDisposable
{
	private readonly List<TemporaryDirectory> _temporaryDirectories = [];

	[Test]
	public async Task RunAsync_returns_not_found_result_when_executable_is_missing()
	{
		GitCliRunner runner = new("agentterminal-missing-git.exe", TimeSpan.FromSeconds(1));

		var result = await runner.RunAsync(
			CreateTempDirectory(),
			["status"],
			outputLine: null,
			CancellationToken.None);

		result.ExitCode.ShouldBe(-1);
		result.Succeeded.ShouldBeFalse();
		result.StandardError.ShouldContain("git.exe not found in PATH");
	}

	[Test]
	public async Task RunAsync_streams_stdout_and_stderr_and_returns_buffers()
	{
		GitCliRunner runner = new("powershell", TimeSpan.FromSeconds(10));
		ConcurrentQueue<string> streamed = [];

		var result = await runner.RunAsync(
			CreateTempDirectory(),
			[
				"-NoProfile",
				"-Command",
				"[Console]::Out.WriteLine('out-one'); [Console]::Error.WriteLine('err-one'); exit 3"
			],
			new ImmediateProgress(streamed.Enqueue),
			CancellationToken.None);

		result.ExitCode.ShouldBe(3);
		result.Succeeded.ShouldBeFalse();
		result.StandardOutput.ShouldContain("out-one");
		result.StandardError.ShouldContain("err-one");
		streamed.ShouldContain("out-one");
		streamed.ShouldContain("err-one");
	}

	[Test]
	public async Task RunAsync_kills_process_tree_on_timeout()
	{
		using var root = TemporaryDirectory.Create();
		var childPidPath = Path.Combine(root.Path, "child-pid.txt");
		var temporaryPidPath = Path.Combine(root.Path, "child-pid.tmp");
		GitCliRunner runner = new("pwsh", TimeSpan.FromSeconds(5));
		TaskCompletionSource pidPublished = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using FileSystemWatcher watcher = new(root.Path, Path.GetFileName(childPidPath))
		{
			EnableRaisingEvents = true,
		};
		watcher.Created += (_, _) => pidPublished.TrySetResult();
		watcher.Renamed += (_, _) => pidPublished.TrySetResult();
		Process? child = null;

		try
		{
			var run = runner.RunAsync(
				root.Path,
				[
					"-NoProfile",
					"-Command",
					$"$child = Start-Process pwsh -ArgumentList @('-NoProfile','-Command','Start-Sleep -Seconds 30') -NoNewWindow -PassThru; "
					+ $"[System.IO.File]::WriteAllText('{temporaryPidPath}', [string]$child.Id); "
					+ $"[System.IO.File]::Move('{temporaryPidPath}', '{childPidPath}'); "
					+ "Wait-Process -Id $child.Id"
				],
				outputLine: null,
				CancellationToken.None);
			if (File.Exists(childPidPath))
			{
				pidPublished.TrySetResult();
			}

			await pidPublished.Task.WaitAsync(TimeSpan.FromSeconds(10));
			var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));
			child = Process.GetProcessById(childPid);
			var result = await run;

			result.ExitCode.ShouldBe(-1);
			result.StandardError.ShouldContain("timed out");
			await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
			child.HasExited.ShouldBeTrue();
		}
		finally
		{
			if (child is not null)
			{
				TryKill(child);
				child.Dispose();
			}
		}
	}

	private string CreateTempDirectory()
	{
		var directory = TemporaryDirectory.Create();
		_temporaryDirectories.Add(directory);
		return directory.Path;
	}

	public void Dispose() => _temporaryDirectories.ForEach(static directory => directory.Dispose());

	private static void TryKill(Process process)
	{
		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch (InvalidOperationException)
		{
		}
	}

	private sealed class ImmediateProgress : IProgress<string>
	{
		private readonly Action<string> _report;

		public ImmediateProgress(Action<string> report)
		{
			_report = report;
		}

		public void Report(string value) => _report(value);
	}
}

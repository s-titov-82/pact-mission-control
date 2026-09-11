using System.ComponentModel;
using System.Diagnostics;

namespace Pact.Updater;

internal interface IProcessLauncher
{
	string? GetExecutablePath(int processId);

	Task<int?> WaitForExitAsync(
		int processId,
		TimeSpan timeout,
		CancellationToken token);

	Task<int> RunSetupAsync(
		string setupPath,
		IReadOnlyList<string> arguments,
		CancellationToken token);

	void StartPact(string executablePath, IReadOnlyList<string> arguments);
}

internal sealed class ProcessLauncher : IProcessLauncher
{
	public string? GetExecutablePath(int processId)
	{
		try
		{
			using var process = Process.GetProcessById(processId);
			return process.HasExited ? null : process.MainModule?.FileName;
		}
		catch (ArgumentException)
		{
			return null;
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	public async Task<int?> WaitForExitAsync(
		int processId,
		TimeSpan timeout,
		CancellationToken token)
	{
		Process process;
		try
		{
			process = Process.GetProcessById(processId);
		}
		catch (ArgumentException)
		{
			return 0;
		}

		using (process)
		{
			try
			{
				await process.WaitForExitAsync(token)
					.WaitAsync(timeout, token)
					.ConfigureAwait(false);
				return process.ExitCode;
			}
			catch (TimeoutException)
			{
				return null;
			}
		}
	}

	public async Task<int> RunSetupAsync(
		string setupPath,
		IReadOnlyList<string> arguments,
		CancellationToken token)
	{
		using var process = Start(setupPath, arguments);
		await process.WaitForExitAsync(token).ConfigureAwait(false);
		return process.ExitCode;
	}

	public void StartPact(string executablePath, IReadOnlyList<string> arguments)
	{
		using var process = Start(executablePath, arguments);
	}

	private static Process Start(string executablePath, IReadOnlyList<string> arguments)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
		ArgumentNullException.ThrowIfNull(arguments);
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(executablePath)
				?? throw new ArgumentException(
					"The executable path must have a parent directory.",
					nameof(executablePath))
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		return Process.Start(startInfo)
			?? throw new Win32Exception("The requested process did not start.");
	}
}

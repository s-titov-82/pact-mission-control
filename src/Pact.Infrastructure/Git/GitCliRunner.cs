using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Pact.Infrastructure.Git;

/// <summary>
/// Runs git as a child process and captures its result.
/// </summary>
public interface IGitCliRunner
{
	/// <summary>
	/// Runs git with the given arguments.
	/// </summary>
	/// <param name="workingDirectory">Repository directory to run in.</param>
	/// <param name="arguments">
	/// Arguments as separate elements; they are passed through without shell parsing, so values
	/// containing spaces stay single arguments.
	/// </param>
	/// <param name="outputLine">
	/// Receives output lines as they arrive, for live progress, or <see langword="null"/> to
	/// collect them only in the result.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>
	/// The result, including a non-zero exit code. A failing git command is reported here rather
	/// than thrown, since callers surface it to the user.
	/// </returns>
	Task<GitCommandResult> RunAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		IProgress<string>? outputLine,
		CancellationToken cancellationToken);
}

/// <summary>
/// Result of one git invocation.
/// </summary>
/// <param name="ExitCode">Process exit code; zero means success.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error, which git also uses for progress text.</param>
public sealed record GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
	/// <summary>Whether git reported success.</summary>
	public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Default <see cref="IGitCliRunner"/>, running the <c>git</c> executable with a bounded timeout
/// so a command awaiting input cannot hang the UI indefinitely.
/// </summary>
public sealed class GitCliRunner : IGitCliRunner
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

	private readonly string _executable;
	private readonly TimeSpan _timeout;

	/// <summary>
	/// Creates a runner using <c>git</c> from <c>PATH</c> and the default two-minute timeout.
	/// </summary>
	public GitCliRunner()
		: this("git", DefaultTimeout)
	{
	}

	/// <summary>
	/// Creates a runner over a specific executable and timeout.
	/// </summary>
	public GitCliRunner(string executable, TimeSpan timeout)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(executable);

		_executable = executable;
		_timeout = timeout;
	}

	/// <inheritdoc />
	/// <exception cref="Win32Exception">The git executable could not be started.</exception>
	[SuppressMessage(
		"Maintainability",
		"CA1508:Avoid dead conditional code",
		Justification = "Process remains nullable for startup failures; nullable flow requires guarded disposal while CA1508 incorrectly treats the successful assignment as unconditional.")]
	public async Task<GitCommandResult> RunAsync(
		string workingDirectory,
		IReadOnlyList<string> arguments,
		IProgress<string>? outputLine,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		ArgumentNullException.ThrowIfNull(arguments);

		StringBuilder standardOutput = new();
		StringBuilder standardError = new();
		object sync = new();

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_timeout);

		Process? process = null;

		try
		{
			ProcessStartInfo startInfo = new(_executable)
			{
				WorkingDirectory = workingDirectory,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
				UseShellExecute = false
			};
			startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
			foreach (var argument in arguments)
			{
				startInfo.ArgumentList.Add(argument);
			}

			process = Process.Start(startInfo)
				?? throw new InvalidOperationException("Unable to start git.exe.");
			process.OutputDataReceived += (_, e) =>
			{
				if (e.Data is null)
				{
					return;
				}

				lock (sync)
				{
					standardOutput.AppendLine(e.Data);
				}

				outputLine?.Report(e.Data);
			};
			process.ErrorDataReceived += (_, e) =>
			{
				if (e.Data is null)
				{
					return;
				}

				lock (sync)
				{
					standardError.AppendLine(e.Data);
				}

				outputLine?.Report(e.Data);
			};

			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
			process.WaitForExit();

			lock (sync)
			{
				return new GitCommandResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			KillProcessTree(process);
			return new GitCommandResult(-1, standardOutput.ToString(), "git command timed out");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			KillProcessTree(process);
			throw;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
		{
			return new GitCommandResult(-1, string.Empty, "git.exe not found in PATH");
		}
		finally
		{
			process?.Dispose();
		}
	}

	private static void KillProcessTree(Process? process)
	{
		if (process is null)
		{
			return;
		}

		try
		{
			if (!process.HasExited)
			{
				process.Kill(entireProcessTree: true);
				process.WaitForExit();
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
		}
	}
}

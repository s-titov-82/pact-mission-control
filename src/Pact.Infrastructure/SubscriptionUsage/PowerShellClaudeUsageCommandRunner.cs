using System.ComponentModel;
using System.Diagnostics;

namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Runs an agent's usage command through PowerShell, so profile-defined wrappers resolve.
/// </summary>
/// <remarks>
/// The command is bounded by a timeout and its process tree is killed on expiry: usage commands
/// occasionally block awaiting input, which would otherwise hang the refresh loop forever.
/// </remarks>
public sealed class PowerShellClaudeUsageCommandRunner : IClaudeUsageCommandRunner
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);
	private static readonly TimeSpan OutputDrainTimeout = TimeSpan.FromSeconds(1);
	private readonly TimeSpan _timeout;
	private readonly IClaudeUsageProcessFactory _processFactory;

	/// <summary>Creates a runner with the default timeout.</summary>
	public PowerShellClaudeUsageCommandRunner()
		: this(DefaultTimeout)
	{
	}

	/// <summary>Creates a runner with a specific timeout.</summary>
	public PowerShellClaudeUsageCommandRunner(TimeSpan timeout)
		: this(timeout, new ClaudeUsageProcessFactory())
	{
	}

	internal PowerShellClaudeUsageCommandRunner(
		TimeSpan timeout,
		IClaudeUsageProcessFactory processFactory)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);

		_timeout = timeout;
		_processFactory = processFactory;
	}

	/// <inheritdoc />
	public async Task<ClaudeUsageCommandResult> RunAsync(
		string commandName,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commandName);

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(_timeout);
		IClaudeUsageProcess? process = null;
		Task<string>? outputTask = null;
		Task<string>? errorTask = null;

		try
		{
			ProcessStartInfo startInfo = new("pwsh")
			{
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false
			};
			startInfo.ArgumentList.Add("-NoLogo");
			startInfo.ArgumentList.Add("-Command");
			startInfo.ArgumentList.Add($"{commandName} -p /usage");

			process = _processFactory.Start(startInfo);

			outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
			errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
			await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

			var output = await outputTask.ConfigureAwait(false);
			var error = await errorTask.ConfigureAwait(false);
			return new ClaudeUsageCommandResult(
				process.ExitCode == 0,
				output,
				error,
				process.ExitCode == 0 ? null : $"Claude usage command exited with code {process.ExitCode}.",
				DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			KillProcessTree(process);
			(var output, var error) = await DrainOutputAsync(outputTask, errorTask).ConfigureAwait(false);
			return new ClaudeUsageCommandResult(
				Succeeded: false,
				StandardOutput: output,
				StandardError: error,
				FailureMessage: "Claude usage command timed out.",
				DateTimeOffset.UtcNow);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			KillProcessTree(process);
			await DrainOutputAsync(outputTask, errorTask).ConfigureAwait(false);
			throw;
		}
		catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
		{
			return new ClaudeUsageCommandResult(
				false,
				string.Empty,
				string.Empty,
				ex.Message,
				DateTimeOffset.UtcNow);
		}
		finally
		{
			try
			{
				process?.Dispose();
			}
			catch (Exception)
			{
			}
		}
	}

	private static async Task<(string Output, string Error)> DrainOutputAsync(
		Task<string>? outputTask,
		Task<string>? errorTask)
	{
		if (outputTask is null || errorTask is null)
		{
			return (string.Empty, string.Empty);
		}

		try
		{
			await Task.WhenAll(outputTask, errorTask)
				.WaitAsync(OutputDrainTimeout)
				.ConfigureAwait(false);
		}
		catch (Exception)
		{
		}

		return (
			outputTask.Status == TaskStatus.RanToCompletion ? outputTask.Result : string.Empty,
			errorTask.Status == TaskStatus.RanToCompletion ? errorTask.Result : string.Empty);
	}

	private static void KillProcessTree(IClaudeUsageProcess? process)
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
			}
		}
		catch (Exception)
		{
		}
	}
}

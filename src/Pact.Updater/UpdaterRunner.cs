using System.Text.Json;
using System.Security.Cryptography;
using Pact.Core.Updates;

namespace Pact.Updater;

internal sealed class UpdaterRunner
{
	private readonly IProcessLauncher _processLauncher;
	private readonly IInstalledFileReleaseGate _fileReleaseGate;
	private readonly TimeSpan _sourceExitTimeout;
	private readonly TimeSpan _fileReleaseTimeout;

	public UpdaterRunner(
		IProcessLauncher processLauncher,
		TimeSpan sourceExitTimeout)
		: this(
			processLauncher,
			new InstalledFileReleaseGate(),
			sourceExitTimeout,
			TimeSpan.FromSeconds(30))
	{
	}

	public UpdaterRunner(
		IProcessLauncher processLauncher,
		IInstalledFileReleaseGate fileReleaseGate,
		TimeSpan sourceExitTimeout,
		TimeSpan fileReleaseTimeout)
	{
		_processLauncher = processLauncher
			?? throw new ArgumentNullException(nameof(processLauncher));
		_fileReleaseGate = fileReleaseGate
			?? throw new ArgumentNullException(nameof(fileReleaseGate));
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			sourceExitTimeout,
			TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			fileReleaseTimeout,
			TimeSpan.Zero);
		_sourceExitTimeout = sourceExitTimeout;
		_fileReleaseTimeout = fileReleaseTimeout;
	}

	public async Task<int> RunAsync(
		UpdaterOptions options,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(options);
		SoftRestartTicket ticket;
		try
		{
			ticket = await UpdaterTicketStore.LoadPendingAsync(
				options.TicketPath,
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException
			or JsonException
			or ArgumentException)
		{
			return 2;
		}

		var liveExecutablePath = _processLauncher.GetExecutablePath(ticket.SourceProcessId);
		if (liveExecutablePath is not null
			&& !string.Equals(
				Path.GetFullPath(liveExecutablePath),
				Path.GetFullPath(ticket.ExecutablePath),
				StringComparison.OrdinalIgnoreCase))
		{
			return 2;
		}

		var sourceExit = await _processLauncher.WaitForExitAsync(
			ticket.SourceProcessId,
			_sourceExitTimeout,
			cancellationToken).ConfigureAwait(false);
		if (sourceExit is null)
		{
			UpdaterTicketStore.DeleteExact(options.TicketPath);
			return 3;
		}

		if (ticket.Mode == SoftRestartMode.ApplyUpdate)
		{
			return await ApplyUpdateAsync(
				options.TicketPath,
				ticket,
				cancellationToken).ConfigureAwait(false);
		}
		if (ticket.Mode != SoftRestartMode.RestartOnly)
		{
			return 2;
		}

		await UpdaterTicketStore.RewriteOutcomeAsync(
			options.TicketPath,
			new SoftRestartOutcome(SoftRestartOutcomeKind.Restarted, null),
			cancellationToken).ConfigureAwait(false);
		try
		{
			_processLauncher.StartPact(ticket.ExecutablePath, BuildRelaunchArguments(ticket));
			return 0;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.ComponentModel.Win32Exception
			or InvalidOperationException)
		{
			return 4;
		}
	}

	private async Task<int> ApplyUpdateAsync(
		string ticketPath,
		SoftRestartTicket ticket,
		CancellationToken cancellationToken)
	{
		var installedPaths = new[]
		{
			ticket.ExecutablePath,
			Path.Combine(ticket.InstallationDirectory, "conpty", "OpenConsole.exe")
		};
		var release = await _fileReleaseGate.WaitAsync(
			installedPaths,
			_fileReleaseTimeout,
			cancellationToken).ConfigureAwait(false);
		if (!release.Released)
		{
			TryDeleteSetup(ticket.SetupPath!);
			return await FailAndRelaunchAsync(
				ticketPath,
				ticket,
				"installed-files-locked",
				cancellationToken).ConfigureAwait(false);
		}

		var setupPath = ticket.SetupPath!;
		string actualHash;
		try
		{
			await using FileStream setup = new(
				setupPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.Read,
				bufferSize: 16384,
				FileOptions.Asynchronous | FileOptions.SequentialScan);
			actualHash = Convert.ToHexString(
					await SHA256.HashDataAsync(setup, cancellationToken).ConfigureAwait(false))
				.ToLowerInvariant();
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException)
		{
			TryDeleteSetup(setupPath);
			return await FailAndRelaunchAsync(
				ticketPath,
				ticket,
				"setup-hash-unavailable",
				cancellationToken).ConfigureAwait(false);
		}
		if (!string.Equals(actualHash, ticket.SetupSha256, StringComparison.Ordinal))
		{
			TryDeleteSetup(setupPath);
			return await FailAndRelaunchAsync(
				ticketPath,
				ticket,
				"setup-hash-mismatch",
				cancellationToken).ConfigureAwait(false);
		}

		var logPath = Path.Combine(Path.GetDirectoryName(ticketPath)!, "setup.log");
		int? setupExitCode = null;
		string? setupFailure = null;
		try
		{
			setupExitCode = await _processLauncher.RunSetupAsync(
				setupPath,
				BuildSetupArguments(ticket.InstallationDirectory, logPath),
				cancellationToken).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.ComponentModel.Win32Exception
			or InvalidOperationException)
		{
			setupFailure = "setup-launch-failed";
		}
		finally
		{
			File.Delete(setupPath);
		}
		if (setupFailure is not null)
		{
			return await FailAndRelaunchAsync(
				ticketPath,
				ticket,
				setupFailure,
				cancellationToken).ConfigureAwait(false);
		}
		if (setupExitCode != 0)
		{
			return await FailAndRelaunchAsync(
				ticketPath,
				ticket,
				"setup-exit-nonzero",
				cancellationToken).ConfigureAwait(false);
		}

		await UpdaterTicketStore.RewriteOutcomeAsync(
			ticketPath,
			new SoftRestartOutcome(SoftRestartOutcomeKind.UpdateApplied, null),
			cancellationToken).ConfigureAwait(false);
		try
		{
			_processLauncher.StartPact(ticket.ExecutablePath, BuildRelaunchArguments(ticket));
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.ComponentModel.Win32Exception
			or InvalidOperationException)
		{
			await UpdaterTicketStore.RewriteAppliedRelaunchFailureAsync(
				ticketPath,
				cancellationToken).ConfigureAwait(false);
			return 6;
		}
		return 0;
	}

	private static void TryDeleteSetup(string setupPath)
	{
		try
		{
			File.Delete(setupPath);
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	private async Task<int> FailAndRelaunchAsync(
		string ticketPath,
		SoftRestartTicket ticket,
		string errorCategory,
		CancellationToken cancellationToken)
	{
		await UpdaterTicketStore.RewriteOutcomeAsync(
			ticketPath,
			new SoftRestartOutcome(
				SoftRestartOutcomeKind.UpdateNotApplied,
				errorCategory),
			cancellationToken).ConfigureAwait(false);
		try
		{
			_processLauncher.StartPact(ticket.ExecutablePath, BuildRelaunchArguments(ticket));
			return 5;
		}
		catch (Exception exception) when (exception is IOException
			or UnauthorizedAccessException
			or System.ComponentModel.Win32Exception
			or InvalidOperationException)
		{
			return 6;
		}
	}

	internal static IReadOnlyList<string> BuildSetupArguments(
		string installationDirectory,
		string logPath) =>
	[
		"/SP-",
		"/SILENT",
		"/NORESTART",
		"/NORESTARTAPPLICATIONS",
		"/NOCLOSEAPPLICATIONS",
		$"/DIR={installationDirectory}",
		$"/LOG={logPath}"
	];

	private static List<string> BuildRelaunchArguments(SoftRestartTicket ticket)
	{
		List<string> arguments = [];
		if (ticket.PassDataRoot)
		{
			arguments.Add("--data-root");
			arguments.Add(ticket.DataRoot);
		}
		arguments.Add("--soft-restart-id");
		arguments.Add(ticket.RestartId);
		if (ticket.SoftRestartProbeOutputPath is { } probePath)
		{
			arguments.Add("--soft-restart-probe-output");
			arguments.Add(probePath);
		}
		return arguments;
	}
}

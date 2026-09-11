using System.Text.Json;
using Pact.Core.Updates;

namespace Pact.Updater;

internal sealed class UpdaterRunner
{
	private readonly IProcessLauncher _processLauncher;
	private readonly TimeSpan _sourceExitTimeout;

	public UpdaterRunner(
		IProcessLauncher processLauncher,
		TimeSpan sourceExitTimeout)
	{
		_processLauncher = processLauncher
			?? throw new ArgumentNullException(nameof(processLauncher));
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			sourceExitTimeout,
			TimeSpan.Zero);
		_sourceExitTimeout = sourceExitTimeout;
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

using System.Diagnostics;
using Pact.Core.Updates;
using Pact.Infrastructure.Updates;

namespace Pact.App.Avalonia.Controllers;

internal sealed record SoftRestartRequestResult(
	bool Started,
	IReadOnlyList<SoftRestartBlocker> Blockers,
	string? Error);

internal sealed class SoftRestartCoordinator
{
	private const int TicketSchemaVersion = 1;
	private readonly AppLaunchOptions _launchOptions;
	private readonly ISoftRestartTicketStore _ticketStore;
	private readonly SoftRestartSafetyPolicy _safetyPolicy;
	private readonly SoftRestartSnapshotBuilder _snapshotBuilder;
	private readonly Func<string> _getExecutablePath;
	private readonly Func<int> _getProcessId;
	private readonly Func<string, string, CancellationToken, Task> _copyHelperAsync;
	private readonly Action<string, string> _startHelper;

	public SoftRestartCoordinator(
		AppLaunchOptions launchOptions,
		ISoftRestartTicketStore ticketStore,
		SoftRestartSafetyPolicy safetyPolicy,
		SoftRestartSnapshotBuilder snapshotBuilder,
		Func<string>? getExecutablePath = null,
		Func<int>? getProcessId = null,
		Func<string, string, CancellationToken, Task>? copyHelperAsync = null,
		Action<string, string>? startHelper = null)
	{
		_launchOptions = launchOptions ?? throw new ArgumentNullException(nameof(launchOptions));
		_ticketStore = ticketStore ?? throw new ArgumentNullException(nameof(ticketStore));
		_safetyPolicy = safetyPolicy ?? throw new ArgumentNullException(nameof(safetyPolicy));
		_snapshotBuilder = snapshotBuilder ?? throw new ArgumentNullException(nameof(snapshotBuilder));
		_getExecutablePath = getExecutablePath ?? GetCurrentExecutablePath;
		_getProcessId = getProcessId ?? (static () => Environment.ProcessId);
		_copyHelperAsync = copyHelperAsync ?? CopyHelperAsync;
		_startHelper = startHelper ?? StartHelper;
	}

	public async Task<SoftRestartRequestResult> RequestRestartOnlyAsync(
		CancellationToken cancellationToken)
	{
		var firstSafety = _safetyPolicy.Evaluate();
		if (!firstSafety.CanRestart)
		{
			return new SoftRestartRequestResult(false, firstSafety.Blockers, null);
		}

		var restartId = SoftRestartTicketStore.CreateRestartId();
		var executablePath = Path.GetFullPath(_getExecutablePath());
		var installationDirectory = Path.GetDirectoryName(executablePath)
			?? throw new InvalidOperationException("The running Pact executable has no parent directory.");
		var snapshot = _snapshotBuilder.Capture();
		SoftRestartTicket ticket = new(
			TicketSchemaVersion,
			restartId,
			SoftRestartMode.RestartOnly,
			ExpectedTargetVersion: null,
			_getProcessId(),
			executablePath,
			installationDirectory,
			_launchOptions.Profile.RootDirectory,
			_launchOptions.PassDataRoot,
			SetupPath: null,
			SetupSha256: null,
			new SoftRestartOutcome(SoftRestartOutcomeKind.Pending, null),
			_launchOptions.SoftRestartProbeOutputPath,
			snapshot.LiveTerminalIds,
			snapshot.LoadedWebPageIds,
			snapshot.UnreadTerminalIds,
			snapshot.Selection,
			snapshot.OrchestratorWasRunning);

		string? ticketPath = null;
		try
		{
			ticketPath = await _ticketStore.WriteNewAsync(ticket, cancellationToken)
				.ConfigureAwait(false);
			var helperSource = Path.Combine(installationDirectory, "Pact.Updater.exe");
			var helperDestination = Path.Combine(
				Path.GetDirectoryName(ticketPath)!,
				"Pact.Updater.exe");
			await _copyHelperAsync(helperSource, helperDestination, cancellationToken)
				.ConfigureAwait(false);

			var finalSafety = _safetyPolicy.Evaluate();
			if (!finalSafety.CanRestart)
			{
				await _ticketStore.DeleteExactAsync(restartId, CancellationToken.None)
					.ConfigureAwait(false);
				return new SoftRestartRequestResult(false, finalSafety.Blockers, null);
			}

			_startHelper(helperDestination, ticketPath);
			return new SoftRestartRequestResult(true, [], null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			if (ticketPath is not null)
			{
				await _ticketStore.DeleteExactAsync(restartId, CancellationToken.None)
					.ConfigureAwait(false);
			}
			throw;
		}
		catch (Exception)
		{
			if (ticketPath is not null)
			{
				await _ticketStore.DeleteExactAsync(restartId, CancellationToken.None)
					.ConfigureAwait(false);
			}
			return new SoftRestartRequestResult(false, [], "soft-restart-start-failed");
		}
	}

	private static string GetCurrentExecutablePath() => Environment.ProcessPath
		?? throw new InvalidOperationException("The running Pact executable path is unavailable.");

	private static Task CopyHelperAsync(
		string source,
		string destination,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		File.Copy(source, destination, overwrite: false);
		return Task.CompletedTask;
	}

	private static void StartHelper(string executablePath, string ticketPath)
	{
		ProcessStartInfo startInfo = new(executablePath)
		{
			UseShellExecute = false,
			WorkingDirectory = Path.GetDirectoryName(executablePath)
				?? throw new InvalidOperationException("The copied updater has no parent directory.")
		};
		startInfo.ArgumentList.Add(ticketPath);
		using var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Pact.Updater did not start.");
	}
}

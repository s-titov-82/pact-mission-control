using System.Text.Json;
using Pact.App.Avalonia.Controllers;
using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Diagnostics;

internal sealed class SoftRestartProbeRunner
{
	private readonly AppLaunchOptions _options;

	public SoftRestartProbeRunner(AppLaunchOptions options)
	{
		_options = options ?? throw new ArgumentNullException(nameof(options));
	}

	/// <summary>
	/// Starts the diagnostic handoff on the first process, or writes bounded restoration
	/// evidence in the relaunched process. A null result means graceful restart must begin.
	/// </summary>
	public async Task<int?> RunAsync(
		AvaloniaMainShellController controller,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(controller);
		if (_options.SoftRestartProbeOutputPath is not { } outputPath)
		{
			return 0;
		}

		if (_options.SoftRestartId is null)
		{
			var preparationFailure = await PrepareInitialStateAsync(
				controller,
				cancellationToken);
			if (preparationFailure is not null)
			{
				await WriteFailureAsync(outputPath, preparationFailure, cancellationToken);
				return 1;
			}

			var result = controller.SoftRestartCoordinator is { } restartCoordinator
				? await restartCoordinator.RequestRestartOnlyAsync(cancellationToken)
				: new SoftRestartRequestResult(false, [], "soft-restart-unavailable");
			if (result.Started)
			{
				return null;
			}

			await SoftRestartProbeEvidenceWriter.WriteAsync(
				outputPath,
				new SoftRestartProbeEvidence(
					RestartId: null,
					ProcessId: Environment.ProcessId,
					ExecutablePath: Environment.ProcessPath ?? string.Empty,
					OpenConsoleProcessIds: GetOpenConsoleProcessIds(),
					TerminalProcessIds: GetTerminalProcessIds(controller),
					RestoredTerminalIds: [],
					ColdStartFallbacks: [],
					RestoredWebPageIds: [],
					UnreadTerminalIds: GetUnreadTerminalIds(controller),
					SelectedItemId: GetSelectedItemId(controller),
					Failures: result.Blockers.Select(item => item.Id)
						.Append(result.Error ?? "soft-restart-blocked")
						.ToArray(),
					OrchestratorRestored: false,
					SelectionRestored: false,
					UpdateFailureCategory: null),
				cancellationToken);
			return 1;
		}

		var summary = controller.LastRestorationSummary;
		if (summary is null)
		{
			await SoftRestartProbeEvidenceWriter.WriteAsync(
				outputPath,
				new SoftRestartProbeEvidence(
					_options.SoftRestartId,
					Environment.ProcessId,
					Environment.ProcessPath ?? string.Empty,
					GetOpenConsoleProcessIds(),
					GetTerminalProcessIds(controller),
					[],
					[],
					[],
					GetUnreadTerminalIds(controller),
					GetSelectedItemId(controller),
					["ticket-not-consumed"],
					false,
					false,
					null),
				cancellationToken);
			return 1;
		}

		await SoftRestartProbeEvidenceWriter.WriteAsync(
			outputPath,
			new SoftRestartProbeEvidence(
				_options.SoftRestartId,
				Environment.ProcessId,
				Environment.ProcessPath ?? string.Empty,
				GetOpenConsoleProcessIds(),
				GetTerminalProcessIds(controller),
				summary.RestoredTerminalIds,
				summary.ColdStartFallbacks,
				summary.RestoredWebPageIds,
				GetUnreadTerminalIds(controller),
				GetSelectedItemId(controller),
				summary.Failures,
				summary.OrchestratorRestored,
				summary.SelectionRestored,
				summary.UpdateFailureCategory),
			cancellationToken);
		return summary.Failures.Count == 0 ? 0 : 1;
	}

	private static async Task<string?> PrepareInitialStateAsync(
		AvaloniaMainShellController controller,
		CancellationToken cancellationToken)
	{
		var workspace = controller.ViewModel.Workspaces.FirstOrDefault(item =>
			item.WebPages.Count > 0
			&& item.Sessions.Any(session =>
				session.Record.Kind is AgentKind.Codex or AgentKind.Claude
				&& AgentResumeCommandExtractor.IsConcreteResumeCommand(
					session.Record.ResumeCommand))
			&& item.Sessions.Any(session =>
				session.Record.Kind is not (AgentKind.Codex or AgentKind.Claude)));
		if (workspace is null)
		{
			return "probe-fixture-unavailable";
		}

		var resumable = workspace.Sessions.First(session =>
			session.Record.Kind is AgentKind.Codex or AgentKind.Claude
			&& AgentResumeCommandExtractor.IsConcreteResumeCommand(
				session.Record.ResumeCommand));
		var nonAgent = workspace.Sessions.First(session =>
			session.Record.Kind is not (AgentKind.Codex or AgentKind.Claude));
		var webPage = workspace.WebPages[0];
		try
		{
			await controller.SelectSessionAsync(
				resumable,
				startIfNeeded: true,
				preferResumeCommand: false,
				cancellationToken);
			await controller.SelectSessionAsync(
				nonAgent,
				startIfNeeded: true,
				preferResumeCommand: false,
				cancellationToken);
			await controller.SelectWebPageAsync(webPage, cancellationToken);
			if (!webPage.IsBrowserLoaded)
			{
				return "probe-browser-not-loaded";
			}
			if (!controller.ViewModel.TerminalTabStatuses.RestoreUnreadCompletion(
				nonAgent.Record.Id,
				DateTimeOffset.UtcNow))
			{
				return "probe-unread-not-seeded";
			}
			await controller.SelectSessionAsync(
				resumable,
				startIfNeeded: false,
				preferResumeCommand: false,
				cancellationToken);
			return null;
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception)
		{
			return "probe-fixture-start-failed";
		}
	}

	private static Task WriteFailureAsync(
		string outputPath,
		string failure,
		CancellationToken cancellationToken) =>
		SoftRestartProbeEvidenceWriter.WriteAsync(
			outputPath,
			new SoftRestartProbeEvidence(
				RestartId: null,
				ProcessId: Environment.ProcessId,
				ExecutablePath: Environment.ProcessPath ?? string.Empty,
				OpenConsoleProcessIds: [],
				TerminalProcessIds: [],
				RestoredTerminalIds: [],
				ColdStartFallbacks: [],
				RestoredWebPageIds: [],
				UnreadTerminalIds: [],
				SelectedItemId: null,
				Failures: [failure],
				OrchestratorRestored: false,
				SelectionRestored: false,
				UpdateFailureCategory: null),
			cancellationToken);

	private static int[] GetTerminalProcessIds(AvaloniaMainShellController controller) =>
		controller.Runtimes.Values
			.Select(runtime => runtime.TryGetController(out var terminal) ? terminal.ProcessId : null)
			.OfType<int>()
			.Distinct()
			.Order()
			.ToArray();

	private static int[] GetOpenConsoleProcessIds() =>
		new SoftRestartProbeProcessSnapshotReader()
			.ReadOpenConsoleChildren(Environment.ProcessId);

	private static string[] GetUnreadTerminalIds(AvaloniaMainShellController controller) =>
		controller.ViewModel.TerminalTabStatuses.GetDiagnosticsSnapshot()
			.Values
			.Where(item => item.HasUnreadCompletion)
			.Select(item => item.SessionId)
			.Order(StringComparer.Ordinal)
			.ToArray();

	private static string? GetSelectedItemId(AvaloniaMainShellController controller) =>
		controller.ViewModel.SelectedSession?.Record.Id
		?? controller.ViewModel.SelectedWebPage?.Record.Id
		?? controller.ViewModel.SelectedProjectNote?.Record.Id;
}

internal static class SoftRestartProbeEvidenceWriter
{
	private const int MaximumItemsPerCategory = 256;
	private const int MaximumItemLength = 160;

	public static async Task WriteAsync(
		string outputPath,
		SoftRestartProbeEvidence evidence,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
		ArgumentNullException.ThrowIfNull(evidence);
		var bounded = evidence with
		{
			OpenConsoleProcessIds = evidence.OpenConsoleProcessIds
				.Take(MaximumItemsPerCategory)
				.ToArray(),
			TerminalProcessIds = evidence.TerminalProcessIds
				.Take(MaximumItemsPerCategory)
				.ToArray(),
			RestoredTerminalIds = Bound(evidence.RestoredTerminalIds),
			ColdStartFallbacks = Bound(evidence.ColdStartFallbacks),
			RestoredWebPageIds = Bound(evidence.RestoredWebPageIds),
			UnreadTerminalIds = Bound(evidence.UnreadTerminalIds),
			Failures = Bound(evidence.Failures)
		};
		Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
		var temporaryPath = $"{outputPath}.{Guid.NewGuid():N}.tmp";
		try
		{
			await using (FileStream stream = new(
				temporaryPath,
				FileMode.CreateNew,
				FileAccess.Write,
				FileShare.None,
				bufferSize: 16384,
				FileOptions.Asynchronous | FileOptions.WriteThrough))
			{
				await JsonSerializer.SerializeAsync(
					stream,
					bounded,
					cancellationToken: cancellationToken);
				await stream.FlushAsync(cancellationToken);
			}
			File.Move(temporaryPath, outputPath, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private static string[] Bound(IEnumerable<string> values) => values
		.Take(MaximumItemsPerCategory)
		.Select(value => value.Length <= MaximumItemLength
			? value
			: value[..MaximumItemLength])
		.ToArray();
}

internal sealed record SoftRestartProbeEvidence(
	string? RestartId,
	int ProcessId,
	string ExecutablePath,
	IReadOnlyList<int> OpenConsoleProcessIds,
	IReadOnlyList<int> TerminalProcessIds,
	IReadOnlyList<string> RestoredTerminalIds,
	IReadOnlyList<string> ColdStartFallbacks,
	IReadOnlyList<string> RestoredWebPageIds,
	IReadOnlyList<string> UnreadTerminalIds,
	string? SelectedItemId,
	IReadOnlyList<string> Failures,
	bool OrchestratorRestored,
	bool SelectionRestored,
	string? UpdateFailureCategory);

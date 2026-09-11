using Pact.Core.Sessions;
using Pact.Core.Updates;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Contains only stable ids and flags required to rebuild runtime surfaces after a restart.
/// </summary>
internal sealed record SoftRestartSnapshot(
	IReadOnlyList<string> LiveTerminalIds,
	IReadOnlyList<string> LoadedWebPageIds,
	IReadOnlyList<string> UnreadTerminalIds,
	SoftRestartSelection? Selection,
	bool OrchestratorWasRunning);

/// <summary>
/// Intersects live runtime identities with the currently active project and ROOT projections.
/// It deliberately never reads terminal text, browser content, prompts, or credentials.
/// </summary>
internal sealed class SoftRestartSnapshotBuilder
{
	private readonly MainWindowViewModel _viewModel;
	private readonly Func<IReadOnlyList<string>> _getActiveSessionIds;
	private readonly Func<IReadOnlyDictionary<string, TerminalClassifierDiagnostics>>
		_getDiagnostics;
	private readonly Func<IReadOnlyList<string>> _getLoadedPageIds;

	public SoftRestartSnapshotBuilder(
		MainWindowViewModel viewModel,
		Func<IReadOnlyList<string>> getActiveSessionIds,
		Func<IReadOnlyDictionary<string, TerminalClassifierDiagnostics>> getDiagnostics,
		Func<IReadOnlyList<string>> getLoadedPageIds)
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_getActiveSessionIds = getActiveSessionIds
			?? throw new ArgumentNullException(nameof(getActiveSessionIds));
		_getDiagnostics = getDiagnostics
			?? throw new ArgumentNullException(nameof(getDiagnostics));
		_getLoadedPageIds = getLoadedPageIds
			?? throw new ArgumentNullException(nameof(getLoadedPageIds));
	}

	public SoftRestartSnapshot Capture()
	{
		var eligibleTerminalIds = GetEligibleTerminalIds();
		var liveTerminalIds = _getActiveSessionIds()
			.Where(eligibleTerminalIds.Contains)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var eligiblePageIds = GetEligiblePageIds();
		var loadedPageIds = _getLoadedPageIds()
			.Where(eligiblePageIds.Contains)
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal)
			.ToArray();
		var diagnostics = _getDiagnostics();
		var unreadTerminalIds = liveTerminalIds
			.Where(id => diagnostics.TryGetValue(id, out var item)
				&& item.HasUnreadCompletion)
			.ToArray();
		var orchestratorId = _viewModel.OrchestratorSlot.Session?.Record.Id;
		var orchestratorWasRunning = _viewModel.OrchestratorSlot.IsRunning
			&& orchestratorId is not null
			&& liveTerminalIds.Contains(orchestratorId, StringComparer.Ordinal);

		return new SoftRestartSnapshot(
			liveTerminalIds,
			loadedPageIds,
			unreadTerminalIds,
			CaptureSelection(eligibleTerminalIds, eligiblePageIds),
			orchestratorWasRunning);
	}

	private HashSet<string> GetEligibleTerminalIds()
	{
		var ids = _viewModel.Workspaces
			.SelectMany(workspace => workspace.Sessions)
			.Select(session => session.Record.Id)
			.ToHashSet(StringComparer.Ordinal);
		foreach (var session in _viewModel.RootTabs.Sessions.Where(item => !item.IsManuallyPaused))
		{
			ids.Add(session.Record.Id);
		}
		if (_viewModel.OrchestratorSlot is { IsRunning: true, Session: { } orchestrator })
		{
			ids.Add(orchestrator.Record.Id);
		}
		return ids;
	}

	private HashSet<string> GetEligiblePageIds()
	{
		var ids = _viewModel.Workspaces
			.SelectMany(workspace => workspace.WebPages)
			.Select(page => page.Record.Id)
			.ToHashSet(StringComparer.Ordinal);
		foreach (var page in _viewModel.RootTabs.WebPages.Where(item => !item.IsManuallyPaused))
		{
			ids.Add(page.Record.Id);
		}
		return ids;
	}

	private SoftRestartSelection? CaptureSelection(
		HashSet<string> eligibleTerminalIds,
		HashSet<string> eligiblePageIds)
	{
		var selectedItemId = _viewModel.SelectedSession?.Record.Id
			?? _viewModel.SelectedWebPage?.Record.Id
			?? _viewModel.SelectedProjectNote?.Record.Id;
		if (selectedItemId is not null
			&& !eligibleTerminalIds.Contains(selectedItemId)
			&& !eligiblePageIds.Contains(selectedItemId)
			&& _viewModel.Workspaces.All(workspace =>
				workspace.Notes.All(note => !string.Equals(
					note.Record.Id,
					selectedItemId,
					StringComparison.Ordinal))))
		{
			return null;
		}

		var selectedProject = _viewModel.SelectedWorkspace;
		if (selectedProject is not null
			&& !_viewModel.Workspaces.Contains(selectedProject))
		{
			return null;
		}
		if (selectedProject is null && selectedItemId is null)
		{
			return null;
		}

		return new SoftRestartSelection(selectedProject?.Id, selectedItemId);
	}
}

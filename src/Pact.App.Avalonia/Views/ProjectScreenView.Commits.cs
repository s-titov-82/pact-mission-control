using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Views.Dialogs;

namespace Pact.App.Avalonia.Views;

internal sealed partial class ProjectScreenView
{
	private void OnCurrentBranchClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is { CanChooseCurrentBranch: true } changes)
		{ _eventTasks.TryRun("changes-current-branch", () => changes.SelectCurrentBranchAsync(CancellationToken.None), static _ => Task.CompletedTask); }
	}
	private void OnWorkingTreeClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is { } changes)
		{ _eventTasks.TryRun("changes-working-tree", () => changes.SelectCommitAsync(null, CancellationToken.None), static _ => Task.CompletedTask); }
	}
	private void OnChooseCommitClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is not { CanChooseCommit: true } changes || TopLevel.GetTopLevel(this) is not Window owner)
		{ return; }
		_eventTasks.TryRun("changes-choose-commit", async () =>
		{
			using var dialog = new GitCommitPickerWindow(changes.CreateCommitPicker(), _eventTasks);
			var commit = await dialog.ShowDialog<GitCommitInfo?>(owner);
			if (commit is not null && ReferenceEquals(changes, _changes))
			{ await changes.SelectCommitAsync(commit, CancellationToken.None); }
		}, static _ => Task.CompletedTask);
	}
}
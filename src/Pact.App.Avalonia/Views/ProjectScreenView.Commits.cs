using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class ProjectScreenView
{
	private const string AutomaticTarget = "Automatic (remote default / main / master)";
	private bool _syncingReviewFilter;
	private IReadOnlyList<GitCommitInfo>? _filterCommits;
	/// <summary>Requests the explicit rebase pull for the project captured at the click.</summary>
	public event EventHandler<WorkspaceViewModel>? PullRebaseRequested;

	private void OnPullRebaseClicked(object? sender, RoutedEventArgs e)
	{
		if (_screen is { Repository: { IsPullVisible: true, IsNotBusy: true } } screen)
		{ PullRebaseRequested?.Invoke(this, screen.Workspace); }
	}
	private void OnReviewScopeClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is { } changes && sender is ToggleButton { Tag: string tag } && Enum.TryParse<GitReviewScope>(tag, out var scope))
		{ _eventTasks.TryRun("changes-review-scope", () => changes.SelectReviewScopeAsync(scope, CancellationToken.None), static _ => Task.CompletedTask); }
	}
	private void OnChooseCommitClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is not { CanChooseCommit: true } changes || TopLevel.GetTopLevel(this) is not Window owner)
		{ return; }
		var intent = changes.ReserveSourceSelection();
		_eventTasks.TryRun("changes-choose-commit", async () =>
		{
			using var dialog = new GitCommitPickerWindow(changes.CreateCommitPicker(), _eventTasks);
			var commit = await dialog.ShowDialog<GitCommitInfo?>(owner);
			if (commit is not null && ReferenceEquals(changes, _changes) && changes.IsSourceSelectionCurrent(intent))
			{ await changes.SelectCommitAsync(commit, CancellationToken.None); }
		}, static _ => Task.CompletedTask);
	}
	private void OnReviewTargetClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is not { CanChooseReviewScopes: true } changes || TopLevel.GetTopLevel(this) is not Window owner)
		{ return; }
		var intent = changes.ReserveSourceSelection();
		var branch = changes.Branch;
		_eventTasks.TryRun("changes-review-target", async () =>
		{
			var branches = await changes.ReadTargetBranchesAsync(CancellationToken.None);
			if (!ReferenceEquals(changes, _changes) || !changes.IsSourceSelectionCurrent(intent) || changes.Branch != branch)
			{ return; }
			var picker = new GitBranchPickDialogViewModel([AutomaticTarget, .. branches], false)
			{ SelectedBranch = changes.SelectedTargetBranch ?? AutomaticTarget };
			var dialog = new GitBranchPickDialog(picker, "Review merge target", "Compare from the common ancestor with this target. No checkout or network operation.", "Choose");
			if (await dialog.ShowDialog<bool>(owner) && dialog.Result is { } result && ReferenceEquals(changes, _changes)
				&& changes.IsSourceSelectionCurrent(intent) && changes.Branch == branch)
			{ await changes.SelectTargetBranchAsync(result.Branch == AutomaticTarget ? null : result.Branch, CancellationToken.None); }
		}, static _ => Task.CompletedTask);
	}
	private void RefreshReviewControls()
	{
		var changes = _changes;
		UncommittedScopeButton.IsChecked = changes is { SourceKind: ReviewSourceKind.Scope, SelectedReviewScope: GitReviewScope.Uncommitted };
		UnmergedScopeButton.IsChecked = changes is { SourceKind: ReviewSourceKind.Scope, SelectedReviewScope: GitReviewScope.Unmerged };
		UnpushedScopeButton.IsChecked = changes is { SourceKind: ReviewSourceKind.Scope, SelectedReviewScope: GitReviewScope.Unpushed };
		_syncingReviewFilter = true;
		try
		{
			if (!ReferenceEquals(_filterCommits, changes?.ScopeCommits))
			{
				_filterCommits = changes?.ScopeCommits;
				ReviewFilter.Items.Clear();
				ReviewFilter.Items.Add(new ComboBoxItem { Content = "All changes" });
				ReviewFilter.Items.Add(new ComboBoxItem { Content = "Uncommitted", Tag = "local" });
				foreach (var commit in _filterCommits ?? [])
				{ ReviewFilter.Items.Add(new ComboBoxItem { Content = $"{commit.ShortId} · {commit.Subject}", Tag = commit }); }
			}
			if (changes?.SelectedCommit is { } selected)
			{
				var item = ReviewFilter.Items.OfType<ComboBoxItem>().FirstOrDefault(candidate => candidate.Tag is GitCommitInfo commit && commit.Id == selected.Id);
				if (item is null)
				{
					// A ref refresh can remove a frozen commit from the new history range.
					item = new ComboBoxItem { Content = $"{selected.ShortId} · {selected.Subject} (selected)", Tag = selected };
					ReviewFilter.Items.Add(item);
				}
				ReviewFilter.SelectedItem = item;
			}
			else
			{ ReviewFilter.SelectedIndex = changes?.IsUncommittedFilter == true ? 1 : 0; }
		}
		finally { _syncingReviewFilter = false; }
	}
	private void OnReviewFilterChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncingReviewFilter || _changes is not { IsScopeHistoryVisible: true } changes || ReviewFilter.SelectedItem is not ComboBoxItem item)
		{ return; }
		_eventTasks.TryRun("changes-review-filter", () => changes.SelectReviewFilterAsync(item.Tag as GitCommitInfo, item.Tag is string, CancellationToken.None), static _ => Task.CompletedTask);
	}
	private void OnMoreScopeCommitsClicked(object? sender, RoutedEventArgs e)
	{
		if (_changes is { } changes)
		{ _eventTasks.TryRun("changes-review-history", () => changes.LoadMoreScopeCommitsAsync(CancellationToken.None), static _ => Task.CompletedTask); }
	}
}
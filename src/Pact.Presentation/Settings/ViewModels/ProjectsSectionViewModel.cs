using System.Collections.ObjectModel;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Projects settings section: one tab per open workspace, each showing project-level fields plus
/// its sessions. Unlike the other sections, this one is not backed by its own JSON file at the
/// item level — it edits live <see cref="WorkspaceViewModel"/>/<see cref="SessionViewModel"/>
/// state via <see cref="IProjectSettingsEditor"/>, which persists through projects.json.
///
/// Reused for both the "Current projects" and "Paused projects" sections: the constructor takes
/// the section enum value, label, description, and workspaces provider, so the same editing UI
/// (and the same <see cref="IProjectSettingsEditor"/> plumbing, which already resolves paused
/// workspaces through <c>MainWindowViewModel</c>'s Workspaces.Concat(PausedWorkspaces) lookups)
/// serves both without duplicating this class.
/// </summary>
public sealed class ProjectsSectionViewModel : SettingsSectionViewModelBase
{
	private static readonly ProjectSettingsEdit EmptyProjectEdit = new();
	private static readonly SessionSettingsEdit EmptySessionEdit = new();

	private readonly Func<IReadOnlyList<WorkspaceViewModel>> _workspacesProvider;
	private readonly IProjectSettingsEditor _editor;
	private readonly Func<Task<string?>> _pickDirectoryAsync;

	/// <summary>
	/// Creates a project list section. The same type serves both the active and paused lists,
	/// parameterized by label, empty-state text, and whether adding is offered.
	/// </summary>
	public ProjectsSectionViewModel(
		Func<IReadOnlyList<WorkspaceViewModel>> workspacesProvider,
		IProjectSettingsEditor editor,
		Func<Task<string?>> pickDirectoryAsync,
		string filePath,
		SettingsSection section = SettingsSection.Projects,
		string label = "Current projects",
		string? description = null,
		string emptyStateText = "No projects. Open a directory from the main window to create one.",
		bool showAddButton = true)
		: base(
			section,
			label,
			description ?? "Open workspaces and their sessions. Edits apply immediately to the running app: project name, root path, notes, and GitLab/TeamCity ids, plus each session's title, working directory, and launch/resume commands.",
			"projects.json",
			filePath)
	{
		ArgumentNullException.ThrowIfNull(workspacesProvider);
		ArgumentNullException.ThrowIfNull(editor);
		ArgumentNullException.ThrowIfNull(pickDirectoryAsync);

		_workspacesProvider = workspacesProvider;
		_editor = editor;
		_pickDirectoryAsync = pickDirectoryAsync;
		EmptyStateText = emptyStateText;
		ShowAddButton = showAddButton;
	}

	/// <summary>Shown in place of the tab strip/form when there are no items; differs between the
	/// "Current projects" and "Paused projects" sections.</summary>
	public string EmptyStateText { get; }

	/// <summary>False for the Paused projects section: adding a project from a directory always
	/// creates an active (non-paused) project, so the "+" button is hidden there.</summary>
	public bool ShowAddButton { get; }

	/// <summary>Projects shown in this section.</summary>
	public ObservableCollection<ProjectItemViewModel> Items { get; } = [];

	/// <summary>Selected project, or <see langword="null"/> when none is selected.</summary>
	public ProjectItemViewModel? SelectedItem
	{
		get;
		set => SetField(ref field, value);
	}

	/// <summary>Rebuilds <see cref="Items"/> from a fresh <see cref="WorkspaceViewModel"/> snapshot, capturing baselines.</summary>
	/// <inheritdoc />
	public override Task LoadAsync(CancellationToken cancellationToken)
	{
		DetachAllItems();
		Items.Clear();
		SelectedItem = null;
		StatusText = null;

		foreach (var workspace in _workspacesProvider())
		{
			ProjectItemViewModel item = new(workspace);
			AttachItem(item);
			Items.Add(item);
		}

		SelectedItem = Items.Count > 0 ? Items[0] : null;
		ClearDirty();
		return Task.CompletedTask;
	}

	/// <summary>
	/// Validates every project and session first (any failure blocks the whole save and leaves
	/// the section dirty). Then applies only the projects/sessions whose diff-based edit is
	/// non-empty; a clean item never reaches the editor. Successfully applied items re-baseline
	/// and clear their dirty state; a failure names the item in the section status while
	/// leaving the section dirty.
	/// </summary>
	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		foreach (var project in Items)
		{
			var projectError = project.Validate();
			if (projectError is not null)
			{
				StatusText = projectError;
				return false;
			}

			foreach (var session in project.Sessions)
			{
				var sessionError = session.Validate();
				if (sessionError is not null)
				{
					StatusText = sessionError;
					return false;
				}
			}
		}

		string? failure = null;
		var appliedCount = 0;
		foreach (var project in Items)
		{
			var projectEdit = project.BuildProjectEdit();
			if (projectEdit != EmptyProjectEdit)
			{
				try
				{
					// No ConfigureAwait(false): Rebaseline() below raises PropertyChanged on
					// UI-bound properties (IsItemDirty, TabHeader), so stay on the UI thread.
					await _editor.UpdateProjectSettingsAsync(project.Id, projectEdit, cancellationToken);
					project.Rebaseline();
					appliedCount++;
				}
				catch (Exception ex)
				{
					failure ??= $"Failed to save project '{project.Name}': {ex.Message}";
				}
			}

			foreach (var session in project.Sessions)
			{
				var sessionEdit = session.BuildSessionEdit();
				if (sessionEdit != EmptySessionEdit)
				{
					try
					{
						// No ConfigureAwait(false): same reason as the project update above.
						await _editor.UpdateSessionSettingsAsync(session.Id, sessionEdit, cancellationToken);
						session.Rebaseline();
						appliedCount++;
					}
					catch (Exception ex)
					{
						failure ??= $"Failed to save session '{session.Title}': {ex.Message}";
					}
				}
			}
		}

		RecomputeDirty();

		if (failure is not null)
		{
			StatusText = failure;
			return false;
		}

		StatusText = $"Saved {Label} ({appliedCount} items).";
		return true;
	}

	/// <summary>
	/// Prompts for a directory; a cancelled pick (null) is a no-op. Otherwise asks the editor to
	/// create (or reuse) the workspace for that directory, reloads, and selects its tab.
	/// </summary>
	public async Task AddProjectAsync(CancellationToken cancellationToken)
	{
		// No ConfigureAwait(false) anywhere in this method: LoadAsync below rebuilds the
		// UI-bound Items collection and SelectItem sets SelectedItem, so every continuation
		// from here on must stay on the captured (dispatcher) SynchronizationContext.
		var directory = await _pickDirectoryAsync();
		if (string.IsNullOrWhiteSpace(directory))
		{
			return;
		}

		var workspaceId = await _editor.CreateProjectForDirectoryAsync(directory, cancellationToken);
		await LoadAsync(cancellationToken);

		if (workspaceId is not null)
		{
			SelectItem(workspaceId, null);
		}
	}

	/// <summary>Selects the project tab (and, if given, the session within it); unknown ids no-op.</summary>
	public override void SelectItem(string? itemId, string? subItemId)
	{
		if (itemId is null)
		{
			return;
		}

		var project = Items.FirstOrDefault(
			item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
		if (project is null)
		{
			return;
		}

		SelectedItem = project;

		if (subItemId is null)
		{
			return;
		}

		var session = project.Sessions.FirstOrDefault(
			item => string.Equals(item.Id, subItemId, StringComparison.Ordinal));
		if (session is not null)
		{
			project.SelectedSession = session;
		}
	}

	private void AttachItem(ProjectItemViewModel item) => item.Changed += OnItemChanged;

	private void DetachItem(ProjectItemViewModel item) => item.Changed -= OnItemChanged;

	private void DetachAllItems()
	{
		foreach (var item in Items)
		{
			DetachItem(item);
		}
	}

	private void OnItemChanged(object? sender, EventArgs e) => RecomputeDirty();

	private void RecomputeDirty() => IsDirty = Items.Any(item => item.IsItemDirty);
}
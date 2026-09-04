using System.Collections.ObjectModel;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Owns the desktop settings sections: which one is active, forwarding deep-link selection
/// into it on load, and tracking whether any section was saved (so the caller knows whether to
/// reload external settings).
/// </summary>
public sealed class SettingsWindowViewModel : SettingsObservableObject
{

	/// <summary>
	/// Builds the settings window over its sections. Optional dependencies are omitted in tests
	/// and by hosts that do not offer the corresponding section.
	/// </summary>
	public SettingsWindowViewModel(
		SettingsFileStore store,
		Func<IReadOnlyList<WorkspaceViewModel>> workspacesProvider,
		IProjectSettingsEditor projectEditor,
		Func<Task<string?>> pickDirectoryAsync,
		Func<IReadOnlyList<WorkspaceViewModel>>? pausedWorkspacesProvider = null,
		AppearanceSettingsStore? appearanceStore = null,
		Action<AppearancePreferences>? applyAppearance = null,
		Func<WebMonitorRule, CancellationToken, Task<WebMonitorTestResult>>?
			testCurrentWebTabAsync = null,
		Func<RootTabsViewModel>? rootTabsProvider = null,
		IRootTabsSettingsEditor? rootTabsEditor = null,
		OrchestratorSectionViewModel? orchestratorSection = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentNullException.ThrowIfNull(workspacesProvider);
		ArgumentNullException.ThrowIfNull(projectEditor);
		ArgumentNullException.ThrowIfNull(pickDirectoryAsync);

		pausedWorkspacesProvider ??= () => [];

		var projectsDescriptor = store.Files.First(
			file => string.Equals(file.FileName, "projects.json", StringComparison.OrdinalIgnoreCase));
		appearanceStore ??= new AppearanceSettingsStore(
			Path.Combine(Path.GetDirectoryName(projectsDescriptor.Path)!, "appearance.json"));
		applyAppearance ??= static _ => { };
		testCurrentWebTabAsync ??= static (_, _) => Task.FromResult(
			new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: "Current-tab testing is not available."));

		List<SettingsSectionViewModelBase> sections =
		[
			new ProjectsSectionViewModel(
				workspacesProvider, projectEditor, pickDirectoryAsync, projectsDescriptor.Path,
				SettingsSection.Projects,
				"Current projects",
				"Open workspaces and their sessions. Edits apply immediately to the running app: project name, root path, notes, and GitLab/TeamCity ids, plus each session's title, working directory, and launch/resume commands.",
				"No projects. Open a directory from the main window to create one.",
				showAddButton: true),
			new ProjectsSectionViewModel(
				pausedWorkspacesProvider, projectEditor, pickDirectoryAsync, projectsDescriptor.Path,
				SettingsSection.PausedProjects,
				"Paused projects",
				"Paused workspaces and their sessions. Editing works the same as Current projects — the same fields apply immediately to the running app — but there is no \"add from directory\" here, since that always creates an active project.",
				"No paused projects.",
				showAddButton: false),
			new LaunchProfilesSectionViewModel(store),
			new ReviewProfilesSectionViewModel(store),
			new WebLinkTemplatesSectionViewModel(store),
			new WebMonitoringRulesSectionViewModel(store, testCurrentWebTabAsync),
			new PromptTemplatesSectionViewModel(store),
			new GitHelpersSectionViewModel(store),
			new ScenariosSectionViewModel(store),
			new RecentFoldersSectionViewModel(store),
			new AppearanceSectionViewModel(appearanceStore, applyAppearance)
		];
		if (orchestratorSection is not null)
		{
			var reviewProfileIndex = sections.FindIndex(
				candidate => candidate.Section == SettingsSection.ReviewProfiles);
			sections.Insert(reviewProfileIndex + 1, orchestratorSection);
		}
		if (rootTabsProvider is not null && rootTabsEditor is not null)
		{
			var rootTabsDescriptor = store.Files.First(
				file => string.Equals(
					file.FileName,
					"root-tabs.json",
					StringComparison.OrdinalIgnoreCase));
			sections.Insert(
				0,
				new RootTabsSectionViewModel(
					rootTabsProvider,
					rootTabsEditor,
					rootTabsDescriptor.Path));
		}

		Sections = new ObservableCollection<SettingsSectionViewModelBase>(sections);
	}

	/// <summary>ROOT tabs is first when its live editor is available, followed by project and
	/// file-backed sections.</summary>
	public ObservableCollection<SettingsSectionViewModelBase> Sections { get; }

	/// <summary>Section currently shown, or <see langword="null"/> before initialization.</summary>
	public SettingsSectionViewModelBase? ActiveSection
	{
		get;
		set => SetField(ref field, value);
	}

	/// <summary>Set the first time any section saves successfully; never cleared.</summary>
	public bool SavedAnyFile
	{
		get;
		private set => SetField(ref field, value);
	}

	/// <summary>Whether any section holds unsaved edits, used to warn before closing.</summary>
	public bool AnyDirty => Sections.Any(section => section.IsDirty);

	/// <summary>
	/// Loads every section, activates <paramref name="section"/> (falling back to the first
	/// section when unknown), and forwards <paramref name="itemId"/>/<paramref name="subItemId"/>
	/// into it as a deep-link selection (a no-op on unknown ids).
	/// </summary>
	public async Task InitializeAsync(SettingsSection section, string? itemId, string? subItemId, CancellationToken cancellationToken)
	{
		// No ConfigureAwait(false): ActiveSection/SelectItem below are UI-bound, and each
		// candidate.LoadAsync() call also mutates that section's own UI-bound state.
		foreach (var candidate in Sections)
		{
			await candidate.LoadAsync(cancellationToken);
		}

		var target = Sections.FirstOrDefault(candidate => candidate.Section == section) ?? Sections[0];
		ActiveSection = target;
		target.SelectItem(itemId, subItemId);
	}

	/// <summary>Saves the active section; sets <see cref="SavedAnyFile"/> only when it succeeds.</summary>
	public async Task<bool> SaveActiveSectionAsync(CancellationToken cancellationToken)
	{
		if (ActiveSection is null)
		{
			return false;
		}

		// No ConfigureAwait(false): SavedAnyFile below is UI-bound.
		var success = await ActiveSection.SaveAsync(cancellationToken);
		if (success)
		{
			SavedAnyFile = true;
		}

		return success;
	}
}

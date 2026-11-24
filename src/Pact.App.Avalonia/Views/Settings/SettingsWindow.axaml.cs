using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Platform;
using Pact.Core.Agents;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.App.Avalonia.Views.Settings;

internal sealed partial class SettingsWindow : Window, IDisposable
{
	private readonly SettingsWindowViewModel? _viewModel;
	private readonly IExternalLauncher? _externalLauncher;
	private readonly Func<MessageDialogRequest, Task<MessageDialogResult>>? _showMessageAsync;
	private readonly Func<Task<string?>> _pickDirectoryAsync = () => Task.FromResult<string?>(null);
	private readonly CancellationTokenSource _lifetimeCancellation = new();
	private readonly ObservedTaskGroup _eventTasks = new(
		static (_, _) => Task.CompletedTask);
	private readonly Func<Exception, Task> _reportUserFailureAsync =
		static _ => Task.CompletedTask;
	private readonly Func<CancellationToken, Task> _checkForUpdatesAsync =
		static _ => Task.CompletedTask;
	private readonly Func<CancellationToken, Task> _openUpdateReleaseNotesAsync =
		static _ => Task.CompletedTask;
	private readonly Func<CancellationToken, Task> _openUpdateContainingFolderAsync =
		static _ => Task.CompletedTask;
	private readonly Func<CancellationToken, Task> _restartAndUpdateAsync =
		static _ => Task.CompletedTask;
	private SettingsSectionViewModelBase? _previousSection;
	private SelectingItemsControl? _tabInput;
	private bool _initialized;
	private bool _closeApproved;
	private bool _closePromptRunning;
	private bool _closing;
	private WindowsAttentionNotification? _testNotification;

	public SettingsWindow()
	{
		InitializeComponent();
		SectionContent.AddHandler(PointerPressedEvent, (_, e) => TrackTabInput(e.Source), RoutingStrategies.Tunnel);
		SectionContent.AddHandler(KeyDownEvent, (_, e) =>
		{
			if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.Tab or Key.Up or Key.Down)
			{
				TrackTabInput(e.Source);
			}
		}, RoutingStrategies.Tunnel);
		SectionContent.AddHandler(SelectingItemsControl.SelectionChangedEvent, OnEditorTabChanged);
		Opened += OnOpened;
		Closing += OnClosing;
		Closed += OnClosed;
	}

	public SettingsWindow(
		SettingsWindowViewModel viewModel,
		IExternalLauncher externalLauncher,
		Func<MessageDialogRequest, Task<MessageDialogResult>>? showMessageAsync = null,
		Func<Task<string?>>? pickDirectoryAsync = null,
		ObservedTaskGroup? eventTasks = null,
		Func<Exception, Task>? reportUserFailureAsync = null,
		Func<CancellationToken, Task>? checkForUpdatesAsync = null,
		Func<CancellationToken, Task>? openUpdateReleaseNotesAsync = null,
		Func<CancellationToken, Task>? openUpdateContainingFolderAsync = null,
		Func<CancellationToken, Task>? restartAndUpdateAsync = null)
		: this()
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
		_showMessageAsync = showMessageAsync ?? (request => MessageDialogWindow.ShowOwnedAsync(this, request));
		_pickDirectoryAsync = pickDirectoryAsync ?? _pickDirectoryAsync;
		_eventTasks = eventTasks ?? _eventTasks;
		_reportUserFailureAsync = reportUserFailureAsync ?? _reportUserFailureAsync;
		_checkForUpdatesAsync = checkForUpdatesAsync ?? _checkForUpdatesAsync;
		_openUpdateReleaseNotesAsync =
			openUpdateReleaseNotesAsync ?? _openUpdateReleaseNotesAsync;
		_openUpdateContainingFolderAsync =
			openUpdateContainingFolderAsync ?? _openUpdateContainingFolderAsync;
		_restartAndUpdateAsync = restartAndUpdateAsync ?? _restartAndUpdateAsync;
		DataContext = viewModel;
		SettingsSection[] navigation = [SettingsSection.Appearance, SettingsSection.Updates, SettingsSection.LaunchProfiles,
			SettingsSection.PromptTemplates, SettingsSection.GitHelpers, SettingsSection.Scenarios,
			SettingsSection.ReviewProfiles, SettingsSection.WebLinkTemplates, SettingsSection.WebMonitoringRules];
		foreach (var section in navigation.SelectMany(id => viewModel.Sections.Where(section => section.Section == id)))
		{
			if (section.GroupHeading is { } heading)
			{
				SectionList.Children.Add(new TextBlock { Text = heading, Classes = { "group-heading" } });
			}
			var button = new Button { Content = section.NavigationLabel, Tag = section };
			button.Click += (_, _) => RunEvent("settings-section-selection", () => TrySelectSectionAsync(section));
			SectionList.Children.Add(button);
			if (section.Section == SettingsSection.LaunchProfiles)
			{
				var row = new DockPanel();
				var indicator = new TextBlock { Text = "↗", HorizontalAlignment = HorizontalAlignment.Right };
				DockPanel.SetDock(indicator, Dock.Right);
				row.Children.Add(indicator);
				row.Children.Add(new TextBlock { Text = "Skill catalog" });
				var catalog = new Button { Content = row };
				AutomationProperties.SetName(catalog, "Skill catalog");
				ToolTip.SetTip(catalog, "Open skill catalog in a separate window");
				catalog.Click += (_, _) => RunEvent("settings-skills", OpenSkillsAsync);
				SectionList.Children.Add(catalog);
				var mcp = new Button { Content = "MCP catalog" };
				AutomationProperties.SetName(mcp, "MCP catalog");
				ToolTip.SetTip(mcp, "Open the MCP server catalog in a separate window");
				mcp.Click += (_, _) => RunEvent("settings-mcp-catalog", OpenMcpCatalogAsync);
				SectionList.Children.Add(mcp);
			}
		}
		viewModel.PropertyChanged += OnWindowViewModelPropertyChanged;
		AddHandler(Button.ClickEvent, OnTemplateButtonClicked, RoutingStrategies.Bubble);
		AddHandler(ComboBox.SelectionChangedEvent, OnProfileSelectionChanged, RoutingStrategies.Bubble);
		foreach (var updates in viewModel.Sections.OfType<UpdatesSectionViewModel>())
		{
			updates.CheckRequested += OnUpdateCheckRequested;
			updates.OpenReleaseNotesRequested += OnUpdateReleaseNotesRequested;
			updates.OpenContainingFolderRequested += OnUpdateContainingFolderRequested;
			updates.RestartAndUpdateRequested += OnRestartAndUpdateRequested;
		}
	}

	public SettingsSection InitialSection { get; set; } = SettingsSection.Projects;

	internal bool IsObjectEditor { get; init; }

	public string? InitialItemId { get; set; }

	public string? InitialSubItemId { get; set; }

	internal Func<AgentProfileRecord, CancellationToken, Task<string>>? CheckProfileAsync { get; init; }

	internal Func<AgentProfileRecord, CancellationToken, Task>? LaunchTestProfileAsync { get; init; }

	private async Task OpenSkillsAsync()
	{
		if (!_initialized || _viewModel is null)
		{
			return;
		}

		var launch = _viewModel.Sections.OfType<LaunchProfilesSectionViewModel>().Single();
		var projects = _viewModel.Sections.OfType<ProjectsSectionViewModel>().ToArray();
		if (launch.IsDirty || projects.Any(section => section.IsDirty))
		{
			StatusText.Text = "Save or revert profile and project edits before opening Skills.";
			return;
		}

		var profiles = launch.Items.OfType<ShellProfileItemViewModel>()
			.Where(p => p.Kind is AgentKind.Claude or AgentKind.Codex)
			.Select(p => p.ToProfile()).ToArray();
		var root = Directory.GetParent(Path.GetDirectoryName(launch.FilePath)!)!.FullName;
		var selected = (_viewModel.ActiveSection as ProjectsSectionViewModel)?.SelectedItem?.RootPath;
		var selectedProfile = _viewModel.ActiveSection == launch ? (launch.SelectedItem as ShellProfileItemViewModel)?.Id : null;
		using var catalog = new SkillCatalogWindow(new AppPaths(root), profiles,
			projects.SelectMany(section => section.Items).Select(p => p.RootPath).ToArray(), selected, selectedProfileId: selectedProfile);
		await catalog.ShowDialog(this);
	}

	private async Task OpenMcpCatalogAsync()
	{
		if (!_initialized || _viewModel is null)
		{
			return;
		}
		var launch = _viewModel.Sections.OfType<LaunchProfilesSectionViewModel>().Single();
		var projects = _viewModel.Sections.OfType<ProjectsSectionViewModel>().ToArray();
		if (launch.IsDirty || projects.Any(section => section.IsDirty))
		{
			StatusText.Text = "Save or revert profile and project edits before opening the MCP catalog.";
			return;
		}
		var profiles = launch.Items.OfType<ShellProfileItemViewModel>().Select(item => item.ToProfile());
		var root = Directory.GetParent(Path.GetDirectoryName(launch.FilePath)!)!.FullName;
		var selectedProject = (_viewModel.ActiveSection as ProjectsSectionViewModel)?.SelectedItem?.RootPath;
		var selectedProfile = _viewModel.ActiveSection == launch ? (launch.SelectedItem as ShellProfileItemViewModel)?.Id : null;
		var catalog = new McpCatalogWindow(new AppPaths(root), profiles,
			projects.SelectMany(section => section.Items).Select(item => item.RootPath), selectedProject, selectedProfile);
		await catalog.ShowDialog(this);
	}

	public bool SavedAnyFile => _viewModel?.SavedAnyFile == true;

	internal async Task InitializeAsync()
	{
		if (_initialized || _viewModel is null)
		{
			return;
		}

		if (IsObjectEditor)
		{
			var section = _viewModel.Sections.Single(candidate => candidate.Section == InitialSection);
			if (section is ProjectsSectionViewModel projects)
			{
				projects.LoadObject(InitialItemId, InitialSubItemId);
			}
			else if (section is RootTabsSectionViewModel tabs)
			{
				tabs.LoadObject(InitialItemId);
			}
			else
			{
				await section.LoadAsync(CancellationToken.None);
			}
			section.SelectItem(InitialItemId, InitialSubItemId);
			_viewModel.ActiveSection = section;
			var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
			MinHeight = 0;
			MinWidth = 380;
			var preferredWidth = InitialSubItemId is not null || section is RootTabsSectionViewModel ? 560 : 760;
			Width = screen is null ? preferredWidth : Math.Min(preferredWidth, screen.WorkingArea.Width / screen.Scaling - 32);
			MaxHeight = screen is null ? 900 : Math.Max(240, screen.WorkingArea.Height / screen.Scaling - 64);
			Height = double.NaN;
			SizeToContent = SizeToContent.Height;
		}
		else
		{
			await _viewModel.InitializeAsync(
				InitialSection, InitialItemId, InitialSubItemId, CancellationToken.None);
		}
		_initialized = true;
		_previousSection = _viewModel.ActiveSection;
		SelectSection(_viewModel.ActiveSection);
	}

	internal void SelectSection(SettingsSectionViewModelBase? section)
	{
		if (_viewModel is null)
		{
			return;
		}

		_viewModel.ActiveSection = section;
		foreach (var button in SectionList.Children.OfType<Button>())
		{
			button.Classes.Set("current", section is not null && ReferenceEquals(button.Tag, section));
		}
		_previousSection = section;
		RefreshActiveSection();
	}

	internal async Task<bool> TrySelectSectionAsync(SettingsSectionViewModelBase target)
	{
		ArgumentNullException.ThrowIfNull(target);
		if (_viewModel is null)
		{
			return false;
		}

		var leaving = _previousSection ?? _viewModel.ActiveSection;
		if (leaving is not null && !ReferenceEquals(leaving, target) && leaving.IsDirty)
		{
			SelectSection(leaving);
			if (!await ResolveUnsavedChangesAsync(leaving, closing: false))
			{
				return false;
			}
		}

		if (leaving is WebMonitoringRulesSectionViewModel monitoring
			&& !ReferenceEquals(leaving, target))
		{
			monitoring.CancelCurrentTest();
		}

		_previousSection = target;
		SelectSection(target);
		return true;
	}

	internal async Task<bool> OpenRawJsonAsync()
	{
		if (_viewModel?.ActiveSection is not { SupportsFileOperations: true } section
			|| _externalLauncher is null)
		{
			return false;
		}

		if (section.IsDirty)
		{
			var choice = await ShowMessageAsync(new MessageDialogRequest(
				"Unsaved changes",
				"Save before opening the file externally?",
				MessageDialogButtons.YesNoCancel,
				MessageDialogResult.Cancel));
			if (choice == MessageDialogResult.Cancel)
			{
				return false;
			}

			if (choice == MessageDialogResult.Yes
				&& !await _viewModel.SaveActiveSectionAsync(CancellationToken.None))
			{
				return false;
			}
		}

		await _externalLauncher.OpenFileAsync(section.FilePath);
		return true;
	}

	internal async Task<bool> TryDeleteItemAsync(SettingsItemViewModelBase item)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (_viewModel is null)
		{
			return false;
		}

		var choice = await ShowMessageAsync(new MessageDialogRequest(
			"Delete entry",
			"Delete this entry? This cannot be undone.",
			MessageDialogButtons.YesNo,
			MessageDialogResult.No));
		if (choice != MessageDialogResult.Yes)
		{
			return false;
		}

		switch (_viewModel.ActiveSection)
		{
			case LaunchProfilesSectionViewModel section:
				section.RemoveItem(item);
				break;
			case ReviewProfilesSectionViewModel section:
				section.RemoveItem(item);
				break;
			case PromptTemplatesSectionViewModel section when item is PromptTemplateItemViewModel template:
				section.RemoveTemplate(template);
				break;
			case WebLinkTemplatesSectionViewModel section:
				section.RemoveItem(item);
				break;
			case WebMonitoringRulesSectionViewModel section:
				section.RemoveItem(item);
				break;
			case ScenariosSectionViewModel section:
				section.RemoveItem(item);
				break;
			case GitHelpersSectionViewModel section:
				section.RemoveItem(item);
				break;
			default:
				return false;
		}

		return true;
	}

	internal static async Task DrainUiQueueAsync() => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

	private void OnOpened(object? sender, EventArgs e) =>
		RunEvent("settings-open", InitializeOpenedAsync);

	private async Task InitializeOpenedAsync()
	{
		await InitializeAsync();
		if (InitialSubItemId is not null)
		{
			await FocusSessionTitleWhenReadyAsync();
		}
	}

	private void OnWindowKeyDown(object? sender, KeyEventArgs e)
	{
		if (_viewModel is null)
		{
			return;
		}

		if (e.Key == Key.S
			&& e.KeyModifiers == KeyModifiers.Control
			&& _viewModel.ActiveSection?.SupportsFileOperations == true)
		{
			e.Handled = true;
			RunEvent("settings-save-shortcut", SaveAsync);
		}
		else if (e.Key == Key.Escape)
		{
			e.Handled = true;
			Close();
		}
	}

	private void TrackTabInput(object? source)
	{
		if (_viewModel?.ActiveSection?.IsDirty != true || source is not Control control)
		{
			return;
		}
		var selector = control.GetSelfAndVisualAncestors().OfType<SelectingItemsControl>().FirstOrDefault();
		if (selector is not TabControl && !(selector is ListBox list
			&& TryGetResource("SettingsTabItemTheme", ActualThemeVariant, out var tabTheme)
			&& ReferenceEquals(list.ItemContainerTheme, tabTheme)))
		{
			return;
		}
		_tabInput = selector;
		Dispatcher.UIThread.Post(() => _tabInput = null, DispatcherPriority.Background);
	}

	private void OnEditorTabChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_tabInput is not { } tabs || !ReferenceEquals(tabs, e.Source)
			|| _viewModel?.ActiveSection is not { IsDirty: true } section
			|| e.RemovedItems.Count != 1 || e.AddedItems.Count != 1)
		{
			return;
		}
		_tabInput = null;
		var target = e.AddedItems[0];
		var index = tabs.SelectedIndex;
		tabs.SelectedItem = e.RemovedItems[0];
		RunEvent("settings-change-tab", async () =>
		{
			if (!await ResolveUnsavedChangesAsync(section, closing: false))
			{
				return;
			}
			// Reloading a file after Discard creates fresh item VMs. Resolve the intended tab again.
			if (target is SettingsItemViewModelBase item && item.Node["id"]?.ToString() is { } id)
			{
				tabs.SelectedItem = tabs.Items.OfType<SettingsItemViewModelBase>()
					.FirstOrDefault(candidate => candidate.Node["id"]?.ToString() == id) ?? tabs.SelectedItem;
			}
			else if (index >= 0 && index < tabs.ItemCount)
			{
				tabs.SelectedIndex = index;
			}
		});
	}

	private void OnOpenRawJsonClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("settings-open-raw-json", OpenRawJsonAsync);

	private void OnRevertClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("settings-revert", RevertAsync);

	private void OnUpdateCheckRequested(object? sender, EventArgs e) =>
		RunEvent(
			"settings-check-for-updates",
			() => _checkForUpdatesAsync(_lifetimeCancellation.Token));

	private void OnUpdateReleaseNotesRequested(object? sender, EventArgs e) =>
		RunEvent(
			"settings-open-update-release-notes",
			() => _openUpdateReleaseNotesAsync(_lifetimeCancellation.Token));

	private void OnUpdateContainingFolderRequested(object? sender, EventArgs e) =>
		RunEvent(
			"settings-open-update-containing-folder",
			() => _openUpdateContainingFolderAsync(_lifetimeCancellation.Token));

	private void OnRestartAndUpdateRequested(object? sender, EventArgs e) =>
		RunEvent(
			"settings-restart-and-update",
			() => _restartAndUpdateAsync(_lifetimeCancellation.Token));

	private async Task RevertAsync()
	{
		if (_viewModel?.ActiveSection is not { SupportsFileOperations: true } section)
		{
			return;
		}

		if (section is WebMonitoringRulesSectionViewModel monitoring)
		{
			monitoring.CancelCurrentTest();
		}

		if (section.IsDirty)
		{
			var choice = await ShowMessageAsync(new MessageDialogRequest(
				"Unsaved changes",
				"Discard unsaved settings changes?",
				MessageDialogButtons.YesNo,
				MessageDialogResult.No));
			if (choice != MessageDialogResult.Yes)
			{
				return;
			}
		}

		await section.ReloadAsync(CancellationToken.None);
		RefreshActiveSection();
	}

	private void OnSectionHelpClicked(object? sender, RoutedEventArgs e)
	{
		if (_viewModel?.ActiveSection is { } section)
		{
			RunEvent(
				"settings-help",
				() => new SettingsHelpWindow(section.Section).ShowDialog(this));
		}
	}

	private void OnSaveClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("settings-save", SaveAsync);

	private void OnProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (e.Source is ComboBox { Tag: "TerminalProfile", DataContext: AppearanceSectionViewModel appearance } picker)
		{
			appearance.SelectProfile(picker.SelectedIndex, ActualThemeVariant == ThemeVariant.Dark);
		}
	}

	private void OnTemplateButtonClicked(object? sender, RoutedEventArgs e)
	{
		if (e.Source is not Button { Tag: string action } button || _viewModel is null)
		{
			return;
		}

		RunEvent(
			"settings-template-action",
			() => RunTemplateActionAsync(button, action));
	}

	private async Task RunTemplateActionAsync(Button button, string action)
	{
		if (_viewModel is null)
		{
			return;
		}

		switch (action)
		{
			case "TestNotification" when button.DataContext is AppearanceSectionViewModel { ShowNotifications: true } preview:
				_testNotification?.Dispose();
				_testNotification = new WindowsAttentionNotification("PactPreview", "Pact · Test notification",
					"This is a preview of your notification settings.", preview.GetPreviewPreferences(),
					Activate, ex => RunEvent("settings-notification-preview", () => _reportUserFailureAsync(ex)));
				break;
			case "LoadTerminalProfiles" when button.DataContext is AppearanceSectionViewModel appearance:
				var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
					"Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState");
				var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
				{
					Title = "Choose Windows Terminal settings.json",
					SuggestedStartLocation = Directory.Exists(folder) ? await StorageProvider.TryGetFolderFromPathAsync(folder) : null,
					FileTypeFilter = [new FilePickerFileType("Terminal settings") { Patterns = ["*.json", "*.jsonc"] }]
				});
				if (files.Count > 0)
				{
					await using var stream = await files[0].OpenReadAsync();
					using StreamReader reader = new(stream);
					appearance.LoadProfiles(await reader.ReadToEndAsync(_lifetimeCancellation.Token));
				}
				break;
			case "CheckProfile" when button.DataContext is ShellProfileItemViewModel profile && CheckProfileAsync is { } check:
				await profile.CheckAsync(check, _lifetimeCancellation.Token);
				break;
			case "LaunchTestProfile" when button.DataContext is ShellProfileItemViewModel profile && LaunchTestProfileAsync is { } launch:
				if (_viewModel.ActiveSection is not LaunchProfilesSectionViewModel { IsDirty: false } profiles
					|| !profiles.Items.Contains(profile))
				{
					StatusText.Text = "Save or revert terminal template edits before opening a test terminal in ROOT.";
					break;
				}
				await profile.CheckAsync(async (snapshot, token) =>
				{
					try
					{
						await launch(snapshot, token);
					}
					catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception or ArgumentException)
					{
						return $"Launch failed. {ex.Message}";
					}
					return CheckProfileAsync is { } refresh
						? await refresh(snapshot, token) : "Check the terminal in ROOT.";
				}, _lifetimeCancellation.Token);
				break;
			case "AddItem":
				switch (button.DataContext)
				{
					case ProjectsSectionViewModel projects:
						await projects.AddProjectAsync(CancellationToken.None);
						break;
					case LaunchProfilesSectionViewModel section:
						section.AddNewItem();
						break;
					case ReviewProfilesSectionViewModel section:
						section.AddNewItem();
						break;
					case WebLinkTemplatesSectionViewModel section:
						section.AddNewItem();
						break;
					case WebMonitoringRulesSectionViewModel section:
						section.AddNewItem();
						break;
					case ScenariosSectionViewModel section:
						section.AddNewItem();
						break;
					case GitHelpersSectionViewModel section:
						section.AddNewItem();
						break;
				}
				break;
			case "AddPrompt" when button.DataContext is PromptTemplateGroupViewModel group
								  && _viewModel.ActiveSection is PromptTemplatesSectionViewModel prompts:
				prompts.AddNewTemplate(group.Type);
				break;
			case "AddDirectory" when button.DataContext is RecentFoldersSectionViewModel recent:
				var path = await _pickDirectoryAsync();
				if (!string.IsNullOrWhiteSpace(path))
				{
					recent.AddDirectory(path);
				}
				break;
			case "Delete" when button.DataContext is SettingsItemViewModelBase item:
				await TryDeleteItemAsync(item);
				break;
			case "AddGitCommand" when button.DataContext is GitHelpersSectionViewModel git:
				git.AddNewCommand();
				break;
			case "MoveGitCommandLeft" when button.DataContext is GitHelpersSectionViewModel git:
				git.MoveSelectedCommand(-1);
				break;
			case "MoveGitCommandRight" when button.DataContext is GitHelpersSectionViewModel git:
				git.MoveSelectedCommand(1);
				break;
			case "OpenGitDoc" when button.DataContext is GitCommandItemViewModel command
								   && _externalLauncher is { } launcher
								   && Uri.TryCreate(command.DocUrlDisplay, UriKind.Absolute, out var uri)
								   && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps):
				await launcher.OpenHttpUriAsync(uri);
				break;
			case "AddGitHelperAction" when button.DataContext is GitHelperItemViewModel helper:
				helper.AddAction();
				break;
			case "RemoveGitHelperAction" when button.DataContext is GitHelperItemViewModel helper
											 && helper.SelectedAction is not null:
				helper.RemoveAction(helper.SelectedAction);
				break;
			case "TestWebMonitorRule"
				when button.DataContext is WebMonitoringRulesSectionViewModel monitoring:
				await monitoring.TestSelectedItemAsync(_lifetimeCancellation.Token);
				break;
			case "CheckForUpdates" when button.DataContext is UpdatesSectionViewModel updates:
				updates.RequestCheck();
				break;
			case "OpenUpdateReleaseNotes" when button.DataContext is UpdatesSectionViewModel updates:
				updates.RequestOpenReleaseNotes();
				break;
			case "OpenUpdateContainingFolder" when button.DataContext is UpdatesSectionViewModel updates:
				updates.RequestOpenContainingFolder();
				break;
			case "RestartAndUpdate" when button.DataContext is UpdatesSectionViewModel updates:
				updates.RequestRestartAndUpdate();
				break;
		}
	}

	private async Task SaveAsync()
	{
		if (_viewModel?.ActiveSection is not { SupportsFileOperations: true })
		{
			return;
		}

		SaveButton.IsEnabled = false;
		try
		{
			if (await _viewModel.SaveActiveSectionAsync(CancellationToken.None) && IsObjectEditor)
			{
				Close();
			}
		}
		finally
		{
			SaveButton.IsEnabled = true;
			RefreshActiveSection();
		}
	}

	private void OnClosing(object? sender, WindowClosingEventArgs e)
	{
		if (_closeApproved || _viewModel?.AnyDirty != true)
		{
			return;
		}

		e.Cancel = true;
		if (_closePromptRunning)
		{
			return;
		}

		_closePromptRunning = true;
		if (!RunEvent("settings-close-prompt", ConfirmCloseAsync))
		{
			_closePromptRunning = false;
		}
	}

	private async Task ConfirmCloseAsync()
	{
		try
		{
			if (_viewModel?.ActiveSection is { } section && await ResolveUnsavedChangesAsync(section, closing: true))
			{
				_closeApproved = true;
				Close();
			}
		}
		finally
		{
			_closePromptRunning = false;
		}
	}

	private async Task<bool> ResolveUnsavedChangesAsync(SettingsSectionViewModelBase section, bool closing)
	{
		var result = await ShowMessageAsync(new MessageDialogRequest(
			"Unsaved changes", $"Save changes to {section.NavigationLabel}?",
			MessageDialogButtons.SaveDiscardReturn, MessageDialogResult.Cancel));
		if (result == MessageDialogResult.Yes)
		{
			return await _viewModel!.SaveActiveSectionAsync(CancellationToken.None);
		}
		if (result != MessageDialogResult.No)
		{
			return false;
		}
		if (!closing)
		{
			await section.ReloadAsync(CancellationToken.None);
		}
		return true;
	}

	private void OnClosed(object? sender, EventArgs e)
	{
		_closing = true;
		_lifetimeCancellation.Cancel();
		foreach (var monitoring in
				 _viewModel?.Sections.OfType<WebMonitoringRulesSectionViewModel>()
				 ?? [])
		{
			monitoring.CancelCurrentTest();
		}
		foreach (var updates in
				 _viewModel?.Sections.OfType<UpdatesSectionViewModel>()
				 ?? [])
		{
			updates.CheckRequested -= OnUpdateCheckRequested;
			updates.OpenReleaseNotesRequested -= OnUpdateReleaseNotesRequested;
			updates.OpenContainingFolderRequested -= OnUpdateContainingFolderRequested;
			updates.RestartAndUpdateRequested -= OnRestartAndUpdateRequested;
			updates.Dispose();
		}

		Dispose();
	}

	private bool RunEvent(string operationName, Func<Task> operation) =>
		!_closing
		&& _eventTasks.TryRun(
			operationName,
			operation,
			_reportUserFailureAsync);

	private Task<MessageDialogResult> ShowMessageAsync(MessageDialogRequest request) =>
		_showMessageAsync?.Invoke(request) ?? Task.FromResult(request.DefaultResult);

	private void OnWindowViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(SettingsWindowViewModel.ActiveSection))
		{
			RefreshActiveSection();
		}
	}

	private void OnActiveSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(SettingsSectionViewModelBase.IsDirty)
			or nameof(SettingsSectionViewModelBase.StatusText))
		{
			RefreshActiveSection();
		}
	}

	private void RefreshActiveSection()
	{
		if (_viewModel is null)
		{
			return;
		}

		foreach (var section in _viewModel.Sections)
		{
			section.PropertyChanged -= OnActiveSectionPropertyChanged;
		}

		var active = _viewModel.ActiveSection;
		active?.PropertyChanged += OnActiveSectionPropertyChanged;

		SectionTitle.Text = active?.NavigationLabel ?? string.Empty;
		StatusText.Text = active?.StatusText ?? string.Empty;
		var supportsFileOperations = active?.SupportsFileOperations == true;
		OpenRawJsonButton.IsVisible = supportsFileOperations;
		RevertButton.IsVisible = supportsFileOperations;
		RevertButton.IsEnabled = supportsFileOperations && active?.IsDirty == true;
		SaveButton.IsVisible = supportsFileOperations;
		if (IsObjectEditor)
		{
			SettingsNavigation.IsVisible = false;
			OpenRawJsonButton.IsVisible = false;
			RevertButton.IsVisible = false;
			Grid.SetColumn(EditorPane, 0);
			Grid.SetColumnSpan(EditorPane, 2);
			EditorPane.Margin = default;
			SectionHelpButton.IsVisible = false;
			object? item = active switch
			{
				ProjectsSectionViewModel { SelectedItem: { } project } when project.Id == InitialItemId =>
					InitialSubItemId is null ? project : project.SelectedTab,
				RootTabsSectionViewModel root => root.SelectedItem,
				_ => null
			};
			var (title, key) = item switch
			{
				SessionSettingsItemViewModel session when session.Id == (InitialSubItemId ?? InitialItemId) =>
					($"Edit session · {session.Title}", "SessionEditorTemplate"),
				RootWebPageSettingsItemViewModel page when page.Id == (InitialSubItemId ?? InitialItemId) =>
					($"Edit web page · {page.Title}", "WebPageEditorTemplate"),
				ProjectItemViewModel project => ($"Edit project · {project.Name}", "ProjectItemTemplate"),
				_ => ("Item unavailable", "")
			};
			Title = SectionTitle.Text = title;
			SaveButton.Content = "Save changes";
			SaveButton.IsEnabled = key.Length > 0;
			SectionContent.ContentTemplate = TryGetResource(key, ActualThemeVariant, out var template) ? template as IDataTemplate : null;
			SectionContent.Content = key.Length > 0 ? item : null;
			return;
		}
		SectionContent.Content = active;
		SectionContent.ContentTemplate = active is null ? null : ResolveSectionTemplate(active.Section);
	}

	private IDataTemplate? ResolveSectionTemplate(SettingsSection section)
	{
		var key = section switch
		{
			SettingsSection.RootTabs => "RootTabsSectionTemplate",
			SettingsSection.Projects => "ProjectsSectionTemplate",
			SettingsSection.PausedProjects => "PausedProjectsSectionTemplate",
			SettingsSection.LaunchProfiles => "LaunchProfilesSectionTemplate",
			SettingsSection.ReviewProfiles => "ReviewProfilesSectionTemplate",
			SettingsSection.Orchestrator => "OrchestratorSectionTemplate",
			SettingsSection.WebLinkTemplates => "WebLinkTemplatesSectionTemplate",
			SettingsSection.WebMonitoringRules => "WebMonitoringRulesSectionTemplate",
			SettingsSection.PromptTemplates => "PromptTemplatesSectionTemplate",
			SettingsSection.GitHelpers => "GitHelpersSectionTemplate",
			SettingsSection.Scenarios => "ScenariosSectionTemplate",
			SettingsSection.RecentFolders => "RecentDirectoriesSectionTemplate",
			SettingsSection.Appearance => "AppearanceSectionTemplate",
			SettingsSection.Updates => "UpdatesSectionTemplate",
			_ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
		};
		return TryGetResource(key, ActualThemeVariant, out var resource)
			? resource as IDataTemplate
			: null;
	}

	internal async Task FocusSessionTitleWhenReadyAsync()
	{
		for (var attempt = 0; attempt < 4; attempt++)
		{
			await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
			UpdateLayout();
			var title = this.GetVisualDescendants()
				.OfType<TextBox>()
				.FirstOrDefault(control => control.Name == "SessionTitleTextBox");
			if (title is null)
			{
				continue;
			}

			var editorScroll = title.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
			var position = editorScroll is null ? null : title.TranslatePoint(default, editorScroll);
			title.BringIntoView();
			if (editorScroll is not null && position is { Y: > 0 })
			{
				editorScroll.Offset = new Vector(
					editorScroll.Offset.X,
					Math.Max(0, editorScroll.Offset.Y + position.Value.Y - 16));
			}
			title.Focus();
			title.SelectAll();
			return;
		}
	}

	public void Dispose()
	{
		_testNotification?.Dispose();
		_lifetimeCancellation.Dispose();
	}
}

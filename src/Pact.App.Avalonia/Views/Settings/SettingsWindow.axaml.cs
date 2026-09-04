using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Platform;
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
	private SettingsSectionViewModelBase? _previousSection;
	private bool _suppressSectionSelection;
	private bool _initialized;
	private bool _closeApproved;
	private bool _closePromptRunning;
	private bool _closing;

	public SettingsWindow()
	{
		InitializeComponent();
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
		Func<Exception, Task>? reportUserFailureAsync = null)
		: this()
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
		_showMessageAsync = showMessageAsync ?? (request => MessageDialogWindow.ShowOwnedAsync(this, request));
		_pickDirectoryAsync = pickDirectoryAsync ?? _pickDirectoryAsync;
		_eventTasks = eventTasks ?? _eventTasks;
		_reportUserFailureAsync = reportUserFailureAsync ?? _reportUserFailureAsync;
		DataContext = viewModel;
		SectionList.ItemsSource = viewModel.Sections;
		viewModel.PropertyChanged += OnWindowViewModelPropertyChanged;
		AddHandler(Button.ClickEvent, OnTemplateButtonClicked, RoutingStrategies.Bubble);
	}

	public SettingsSection InitialSection { get; set; } = SettingsSection.Projects;

	public string? InitialItemId { get; set; }

	public string? InitialSubItemId { get; set; }

	public bool SavedAnyFile => _viewModel?.SavedAnyFile == true;

	internal async Task InitializeAsync()
	{
		if (_initialized || _viewModel is null)
		{
			return;
		}

		await _viewModel.InitializeAsync(
			InitialSection, InitialItemId, InitialSubItemId, CancellationToken.None);
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

		_suppressSectionSelection = true;
		_viewModel.ActiveSection = section;
		SectionList.SelectedItem = section;
		_suppressSectionSelection = false;
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
			var result = await ShowMessageAsync(new MessageDialogRequest(
				"Unsaved changes",
				"Discard unsaved settings changes?",
				MessageDialogButtons.YesNo,
				MessageDialogResult.No));
			if (result != MessageDialogResult.Yes)
			{
				SelectSection(leaving);
				return false;
			}

			await leaving.ReloadAsync(CancellationToken.None);
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
		if (_viewModel?.ActiveSection is not { } section || _externalLauncher is null)
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

		if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
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

	private void OnSectionSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_suppressSectionSelection || SectionList.SelectedItem is not SettingsSectionViewModelBase target)
		{
			return;
		}

		RunEvent(
			"settings-section-selection",
			() => TrySelectSectionAsync(target));
	}

	private void OnOpenRawJsonClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("settings-open-raw-json", OpenRawJsonAsync);

	private void OnRevertClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("settings-revert", RevertAsync);

	private async Task RevertAsync()
	{
		if (_viewModel?.ActiveSection is not { } section)
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
			case "AddReviewer" when button.DataContext is ScenarioItemViewModel scenario:
				scenario.AddInstruction();
				break;
			case "RemoveReviewer" when button.DataContext is ScenarioItemViewModel scenario
									   && scenario.SelectedInstruction is not null:
				scenario.RemoveInstruction(scenario.SelectedInstruction);
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
		}
	}

	private async Task SaveAsync()
	{
		if (_viewModel is null)
		{
			return;
		}

		SaveButton.IsEnabled = false;
		try
		{
			await _viewModel.SaveActiveSectionAsync(CancellationToken.None);
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
		var result = await ShowMessageAsync(new MessageDialogRequest(
			"Unsaved changes",
			"Discard unsaved settings changes?",
			MessageDialogButtons.YesNo,
			MessageDialogResult.No));
		_closePromptRunning = false;
		if (result == MessageDialogResult.Yes)
		{
			_closeApproved = true;
			Close();
		}
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

		_lifetimeCancellation.Dispose();
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

		SectionTitle.Text = active?.Label ?? string.Empty;
		StatusText.Text = active?.StatusText ?? string.Empty;
		RevertButton.IsEnabled = active?.IsDirty == true;
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

	public void Dispose() => _lifetimeCancellation.Dispose();
}

using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class SettingsWindowInteractionTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;
	public void Dispose() => _temporaryDirectory.Dispose();

	[AvaloniaTest]
	public async Task Initialize_materializes_all_sections_in_order_and_selects_session_deep_link()
	{
		(var vm, var workspace) = await CreateViewModelAsync(includeSession: true);
		using var window = CreateWindow(vm);
		window.InitialItemId = workspace.Id;
		window.InitialSubItemId = workspace.Sessions[0].Record.Id;

		await window.InitializeAsync();
		window.Show();
		await window.FocusSessionTitleWhenReadyAsync();

		var sectionList = window.FindControl<ListBox>("SectionList").ShouldBeOfType<ListBox>();
		sectionList.Items.Cast<SettingsSectionViewModelBase>().Select(section => section.Label).ShouldBe([
				"Current projects", "Paused projects", "Terminal templates", "Review profiles",
				"Web link templates", "Web monitoring rules", "Prompt/Shell templates", "Git popup",
				"Scenarios", "Recent directories", "Appearance"
			]);
		foreach (var section in vm.Sections)
		{
			var key = section.Section switch
			{
				SettingsSection.Projects => "ProjectsSectionTemplate",
				SettingsSection.PausedProjects => "PausedProjectsSectionTemplate",
				SettingsSection.LaunchProfiles => "LaunchProfilesSectionTemplate",
				SettingsSection.ReviewProfiles => "ReviewProfilesSectionTemplate",
				SettingsSection.WebLinkTemplates => "WebLinkTemplatesSectionTemplate",
				SettingsSection.WebMonitoringRules => "WebMonitoringRulesSectionTemplate",
				SettingsSection.PromptTemplates => "PromptTemplatesSectionTemplate",
				SettingsSection.GitHelpers => "GitHelpersSectionTemplate",
				SettingsSection.Scenarios => "ScenariosSectionTemplate",
				SettingsSection.RecentFolders => "RecentDirectoriesSectionTemplate",
				SettingsSection.Appearance => "AppearanceSectionTemplate",
				_ => throw new ArgumentOutOfRangeException()
			};
			window.TryGetResource(key, window.ActualThemeVariant, out var resource).ShouldBeTrue();
			resource!.ShouldBeAssignableTo<IDataTemplate>()!.Build(section).ShouldNotBeNull();
		}

		var projects = vm.ActiveSection.ShouldBeOfType<ProjectsSectionViewModel>();
		(projects.SelectedItem?.Id).ShouldBe(workspace.Id);
		(projects.SelectedItem?.SelectedSession?.Id).ShouldBe(workspace.Sessions[0].Record.Id);

		var title = window.GetVisualDescendants().OfType<TextBox>().Where(control => control.Name == "SessionTitleTextBox").ShouldHaveSingleItem();
		title.IsFocused.ShouldBeTrue();
		title.SelectionStart.ShouldBe(0);
		title.SelectionEnd.ShouldBe(title.Text?.Length ?? 0);
		var editorScroll = title.GetVisualAncestors().OfType<ScrollViewer>().First().ShouldBeOfType<ScrollViewer>();
		editorScroll.ShouldNotBeNull();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Repeated_entry_sections_render_horizontal_tab_strips_with_visible_add_actions()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();

		foreach (var sectionId in new[]
				 {
					 SettingsSection.LaunchProfiles,
					 SettingsSection.WebLinkTemplates,
					 SettingsSection.WebMonitoringRules,
					 SettingsSection.Scenarios
				 })
		{
			var section = vm.Sections.Single(item => item.Section == sectionId);
			window.SelectSection(section);
			window.UpdateLayout();
			await DrainUiFourTimesAsync();

			object items = section switch
			{
				LaunchProfilesSectionViewModel launch => launch.Items,
				WebLinkTemplatesSectionViewModel links => links.Items,
				WebMonitoringRulesSectionViewModel monitoring => monitoring.Items,
				ScenariosSectionViewModel scenarios => scenarios.Items,
				_ => throw new InvalidOperationException()
			};
			AssertTabEditor(window, items);
			AssertVisibleButton(window, "AddItem", section);
		}

		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_tabs_add_and_select_a_new_rule()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var before = section.Items.Count;

		var add = window.GetVisualDescendants()
			.OfType<Button>()
			.Where(button =>
				Equals(button.Tag, "AddItem")
				&& ReferenceEquals(button.DataContext, section))
			.ShouldHaveSingleItem();
		add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await DrainUiFourTimesAsync();

		section.Items.Count.ShouldBe(before + 1);
		var added =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var tabs = window.GetVisualDescendants()
			.OfType<ListBox>()
			.Where(control => ReferenceEquals(control.ItemsSource, section.Items))
			.ShouldHaveSingleItem();
		tabs.SelectedItem.ShouldBeSameAs(added);

		await section.ReloadAsync(CancellationToken.None);
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_delete_button_removes_selected_rule_and_selects_remaining_tab()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(
			vm,
			answers: new Queue<MessageDialogResult>([MessageDialogResult.Yes]));
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var before = section.Items.Count;
		var removed = section.SelectedItem.ShouldNotBeNull();
		var tabs = window.GetVisualDescendants()
			.OfType<ListBox>()
			.Where(control => ReferenceEquals(control.ItemsSource, section.Items))
			.ShouldHaveSingleItem();
		var delete = window.GetVisualDescendants()
			.OfType<Button>()
			.Where(button =>
				Equals(button.Tag, "Delete")
				&& ReferenceEquals(button.DataContext, removed))
			.ShouldHaveSingleItem();

		delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => section.Items.Count == before - 1);
		await DrainUiFourTimesAsync();

		section.SelectedItem.ShouldNotBeNull();
		section.SelectedItem.ShouldNotBeSameAs(removed);
		tabs.SelectedItem.ShouldBeSameAs(section.SelectedItem);
		section.SelectedItem.TabHeader.ShouldNotBe(removed.TabHeader);
		section.IsDirty.ShouldBeTrue();

		await section.ReloadAsync(CancellationToken.None);
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_source_dropdowns_are_role_specific_and_revision_selection_is_two_way()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var item =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var activitySource =
			FindNamed<ComboBox>(window, "WebMonitorActivitySourceComboBox");
		var revisionSource =
			FindNamed<ComboBox>(window, "WebMonitorRevisionSourceComboBox");

		activitySource.ItemsSource!.Cast<WebMonitorValueSource>().ShouldBe([
			WebMonitorValueSource.Exists,
			WebMonitorValueSource.Count,
			WebMonitorValueSource.Text,
			WebMonitorValueSource.Attribute
		]);
		revisionSource.ItemsSource!.Cast<WebMonitorValueSource>().ShouldBe([
			WebMonitorValueSource.Text,
			WebMonitorValueSource.Attribute
		]);

		revisionSource.SelectedItem = WebMonitorValueSource.Attribute;
		await DrainUiFourTimesAsync();
		item.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);

		item.RevisionExtractor.Source = WebMonitorValueSource.Text;
		await DrainUiFourTimesAsync();
		revisionSource.SelectedItem.ShouldBe(WebMonitorValueSource.Text);
		window.Close();
	}

	[AvaloniaTest]
	public async Task Persisted_invalid_revision_source_remains_unchanged_and_visible_as_validation_error()
	{
		(var vm, _) = await CreateViewModelAsync();
		var filePath = new AppPaths(_root).WebMonitorRulesPath;
		var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsArray();
		root[0]!["revision"]!["source"] = "count";
		await File.WriteAllTextAsync(
			filePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var item =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var revisionSource =
			FindNamed<ComboBox>(window, "WebMonitorRevisionSourceComboBox");
		var warning =
			FindNamed<TextBlock>(window, "WebMonitorRevisionSourceWarningText");

		revisionSource.ItemsSource!.Cast<WebMonitorValueSource>().ShouldBe([
			WebMonitorValueSource.Text,
			WebMonitorValueSource.Attribute
		]);
		revisionSource.SelectedItem.ShouldBeNull();
		item.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Count);
		((string?)item.Node["revision"]!["source"]).ShouldBe("count");
		warning.IsEffectivelyVisible.ShouldBeTrue();
		(warning.Text ?? string.Empty).ShouldContain("Count");
		section.IsDirty.ShouldBeFalse();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("Revision extractors");
		(section.StatusText ?? string.Empty).ShouldContain("text or attribute");
		item.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Count);
		((string?)item.Node["revision"]!["source"]).ShouldBe("count");
		window.Close();
	}

	[AvaloniaTest]
	public async Task Persisted_unknown_revision_source_has_no_selection_and_is_not_rewritten_on_load()
	{
		(var vm, _) = await CreateViewModelAsync();
		var filePath = new AppPaths(_root).WebMonitorRulesPath;
		var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsArray();
		root[0]!["revision"]!["source"] = "future-source";
		await File.WriteAllTextAsync(
			filePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var item =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var revisionSource =
			FindNamed<ComboBox>(window, "WebMonitorRevisionSourceComboBox");
		var warning =
			FindNamed<TextBlock>(window, "WebMonitorRevisionSourceWarningText");

		revisionSource.SelectedItem.ShouldBeNull();
		item.RevisionExtractor.Source.ShouldBeNull();
		warning.IsEffectivelyVisible.ShouldBeTrue();
		(warning.Text ?? string.Empty).ShouldContain("future-source");
		((string?)item.Node["revision"]!["source"]).ShouldBe("future-source");
		section.IsDirty.ShouldBeFalse();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(section.StatusText ?? string.Empty).ShouldContain("future-source");
		((string?)item.Node["revision"]!["source"]).ShouldBe("future-source");
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_form_binds_all_fields_and_preserves_unknown_extractor_json()
	{
		(var vm, _) = await CreateViewModelAsync();
		var filePath = new AppPaths(_root).WebMonitorRulesPath;
		var root = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!.AsArray();
		root[0]!["activity"]!["futureActivity"] = "keep-activity";
		root[0]!["revision"]!["futureRevision"] = "keep-revision";
		await File.WriteAllTextAsync(
			filePath,
			root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var item =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();

		var enabled = FindNamed<CheckBox>(window, "WebMonitorEnabledCheckBox");
		var id = FindNamed<TextBox>(window, "WebMonitorIdTextBox");
		var title = FindNamed<TextBox>(window, "WebMonitorTitleTextBox");
		var url = FindNamed<TextBox>(window, "WebMonitorUrlPatternTextBox");
		var interval = FindNamed<TextBox>(window, "WebMonitorIntervalTextBox");
		var activityEnabled =
			FindNamed<CheckBox>(window, "WebMonitorActivityEnabledCheckBox");
		var activitySelector =
			FindNamed<TextBox>(window, "WebMonitorActivitySelectorTextBox");
		var activitySource =
			FindNamed<ComboBox>(window, "WebMonitorActivitySourceComboBox");
		var activityAttribute =
			FindNamed<TextBox>(window, "WebMonitorActivityAttributeNameTextBox");
		var activityPattern =
			FindNamed<TextBox>(window, "WebMonitorActivityMatchPatternTextBox");
		var revisionEnabled =
			FindNamed<CheckBox>(window, "WebMonitorRevisionEnabledCheckBox");
		var revisionSelector =
			FindNamed<TextBox>(window, "WebMonitorRevisionSelectorTextBox");
		var revisionSource =
			FindNamed<ComboBox>(window, "WebMonitorRevisionSourceComboBox");
		var revisionAttribute =
			FindNamed<TextBox>(window, "WebMonitorRevisionAttributeNameTextBox");
		var revisionPattern =
			FindNamed<TextBox>(window, "WebMonitorRevisionMatchPatternTextBox");
		var revisionGroup =
			FindNamed<TextBox>(window, "WebMonitorRevisionCaptureGroupTextBox");

		activityEnabled.IsChecked = false;
		revisionEnabled.IsChecked = false;
		await DrainUiFourTimesAsync();
		item.HasActivityExtractor.ShouldBeFalse();
		item.HasRevisionExtractor.ShouldBeFalse();
		((string?)item.Node["activity"]!["futureActivity"]).ShouldBe("keep-activity");
		((string?)item.Node["revision"]!["futureRevision"]).ShouldBe("keep-revision");
		activityEnabled.IsChecked = true;
		revisionEnabled.IsChecked = true;
		await DrainUiFourTimesAsync();

		enabled.IsChecked = true;
		id.Text = "rule-from-form";
		title.Text = "Rule from form";
		url.Text = "^https://example\\.test/";
		interval.Text = "45";
		activitySelector.Text = ".running";
		activitySource.SelectedItem = WebMonitorValueSource.Attribute;
		activityAttribute.Text = "data-state";
		activityPattern.Text = "^running$";
		revisionSelector.Text = ".latest-build";
		revisionSource.SelectedItem = WebMonitorValueSource.Attribute;
		revisionAttribute.Text = "data-build";
		revisionPattern.Text = "build-(\\d+)";
		revisionGroup.Text = "1";
		await DrainUiFourTimesAsync();

		item.Enabled.ShouldBeTrue();
		item.Id.ShouldBe("rule-from-form");
		item.Title.ShouldBe("Rule from form");
		item.UrlPattern.ShouldBe("^https://example\\.test/");
		item.PollIntervalSecondsText.ShouldBe("45");
		item.HasActivityExtractor.ShouldBeTrue();
		item.ActivityExtractor.Selector.ShouldBe(".running");
		item.ActivityExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		item.ActivityExtractor.AttributeName.ShouldBe("data-state");
		item.ActivityExtractor.MatchPattern.ShouldBe("^running$");
		activityAttribute.IsEffectivelyVisible.ShouldBeTrue();
		activityPattern.IsEffectivelyVisible.ShouldBeTrue();
		item.HasRevisionExtractor.ShouldBeTrue();
		item.RevisionExtractor.Selector.ShouldBe(".latest-build");
		item.RevisionExtractor.Source.ShouldBe(WebMonitorValueSource.Attribute);
		item.RevisionExtractor.AttributeName.ShouldBe("data-build");
		item.RevisionExtractor.MatchPattern.ShouldBe("build-(\\d+)");
		item.RevisionExtractor.CaptureGroupText.ShouldBe("1");
		revisionAttribute.IsEffectivelyVisible.ShouldBeTrue();
		revisionPattern.IsEffectivelyVisible.ShouldBeTrue();
		revisionGroup.IsEffectivelyVisible.ShouldBeTrue();

		item.Enabled = false;
		item.Id = "rule-from-view-model";
		item.Title = "Rule from view model";
		item.UrlPattern = "^https://changed\\.example/";
		item.PollIntervalSecondsText = "60";
		item.ActivityExtractor.Selector = ".view-model-running";
		item.ActivityExtractor.Source = WebMonitorValueSource.Text;
		item.ActivityExtractor.MatchPattern = "^busy$";
		item.RevisionExtractor.Selector = ".view-model-revision";
		item.RevisionExtractor.Source = WebMonitorValueSource.Attribute;
		item.RevisionExtractor.AttributeName = "data-revision";
		item.RevisionExtractor.MatchPattern = "(build)-(\\d+)";
		item.RevisionExtractor.CaptureGroupText = "2";
		await DrainUiFourTimesAsync();

		enabled.IsChecked.ShouldBe(false);
		id.Text.ShouldBe("rule-from-view-model");
		title.Text.ShouldBe("Rule from view model");
		url.Text.ShouldBe("^https://changed\\.example/");
		interval.Text.ShouldBe("60");
		activitySelector.Text.ShouldBe(".view-model-running");
		activitySource.SelectedItem.ShouldBe(WebMonitorValueSource.Text);
		activityAttribute.IsEffectivelyVisible.ShouldBeFalse();
		activityPattern.Text.ShouldBe("^busy$");
		activityPattern.IsEffectivelyVisible.ShouldBeTrue();
		revisionSelector.Text.ShouldBe(".view-model-revision");
		revisionSource.SelectedItem.ShouldBe(WebMonitorValueSource.Attribute);
		revisionAttribute.Text.ShouldBe("data-revision");
		revisionPattern.Text.ShouldBe("(build)-(\\d+)");
		revisionGroup.Text.ShouldBe("2");

		item.ActivityExtractor.Source = WebMonitorValueSource.Count;
		item.RevisionExtractor.Source = WebMonitorValueSource.Text;
		await DrainUiFourTimesAsync();

		activitySource.SelectedItem.ShouldBe(WebMonitorValueSource.Count);
		activityAttribute.IsEffectivelyVisible.ShouldBeFalse();
		activityPattern.IsEffectivelyVisible.ShouldBeFalse();
		revisionSource.SelectedItem.ShouldBe(WebMonitorValueSource.Text);
		revisionAttribute.IsEffectivelyVisible.ShouldBeFalse();
		revisionPattern.IsEffectivelyVisible.ShouldBeTrue();
		revisionGroup.IsEffectivelyVisible.ShouldBeTrue();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(filePath))!
			.AsArray()[0]!.AsObject();
		((string?)saved["activity"]!["futureActivity"]).ShouldBe("keep-activity");
		((string?)saved["revision"]!["futureRevision"]).ShouldBe("keep-revision");
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_marker_and_test_button_show_success_without_dirtying_section()
	{
		var calls = 0;
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (rule, _) =>
			{
				calls++;
				return Task.FromResult(new WebMonitorTestResult(
					UrlMatched: true,
					Activity: true,
					Revision: "1842",
					Error: null));
			});
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();

		var marker = FindNamed<TextBlock>(window, "WebMonitorMarkerWarningText");
		marker.IsEffectivelyVisible.ShouldBeTrue();
		(marker.Text ?? string.Empty).ShouldContain("CHANGE-ME-");
		var test = FindNamed<Button>(window, "WebMonitorTestButton");
		ToolTip.GetTip(test).ShouldBe("Evaluate this rule once against the selected loaded web tab");
		test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => section.TestResultMessage is not null);
		await DrainUiFourTimesAsync();

		calls.ShouldBe(1);
		(section.TestResultMessage ?? string.Empty).ShouldContain("matched");
		(section.TestResultMessage ?? string.Empty).ShouldContain("true");
		(section.TestResultMessage ?? string.Empty).ShouldContain("1842");
		section.IsDirty.ShouldBeFalse();
		var result = FindNamed<TextBlock>(window, "WebMonitorTestResultText");
		result.IsEffectivelyVisible.ShouldBeTrue();
		result.TextWrapping.ShouldBe(TextWrapping.Wrap);
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_test_button_renders_delegate_error()
	{
		var calls = 0;
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (_, _) =>
			{
				calls++;
				return Task.FromResult(new WebMonitorTestResult(
					UrlMatched: false,
					Activity: null,
					Revision: null,
					Error: "No loaded web tab is selected."));
			});
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();

		FindNamed<Button>(window, "WebMonitorTestButton")
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => section.TestResultMessage is not null);
		await DrainUiFourTimesAsync();

		calls.ShouldBe(1);
		FindNamed<TextBlock>(window, "WebMonitorTestResultText").Text
			.ShouldBe("No loaded web tab is selected.");
		section.IsDirty.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_test_button_disables_reentry_until_current_test_finishes()
	{
		var calls = 0;
		TaskCompletionSource<WebMonitorTestResult> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (_, _) =>
			{
				calls++;
				return pending.Task;
			});
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var test = FindNamed<Button>(window, "WebMonitorTestButton");

		test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => calls == 1);
		await DrainUiFourTimesAsync();
		section.IsTestInProgress.ShouldBeTrue();
		test.IsEnabled.ShouldBeFalse();

		test.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await DrainUiFourTimesAsync();
		calls.ShouldBe(1);

		pending.SetResult(new WebMonitorTestResult(
			UrlMatched: true,
			Activity: false,
			Revision: "done",
			Error: null));
		await WaitUntilAsync(() => !section.IsTestInProgress);
		(section.TestResultMessage ?? string.Empty).ShouldContain("done");
		test.IsEnabled.ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_selection_change_cancels_test_and_ignores_stale_completion()
	{
		var calls = 0;
		CancellationToken observedToken = default;
		TaskCompletionSource<WebMonitorTestResult> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (_, token) =>
			{
				calls++;
				observedToken = token;
				return pending.Task;
			});
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var next =
			section.Items[1].ShouldBeOfType<WebMonitorRuleItemViewModel>();

		FindNamed<Button>(window, "WebMonitorTestButton")
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => calls == 1);
		section.SelectedItem = next;
		await DrainUiFourTimesAsync();

		observedToken.IsCancellationRequested.ShouldBeTrue();
		section.IsTestInProgress.ShouldBeFalse();
		section.TestResultMessage.ShouldBeNull();
		pending.SetResult(new WebMonitorTestResult(
			UrlMatched: true,
			Activity: true,
			Revision: "stale",
			Error: null));
		await DrainUiFourTimesAsync();
		section.TestResultMessage.ShouldBeNull();
		section.IsDirty.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_revert_cancels_test_and_ignores_stale_completion()
	{
		var calls = 0;
		CancellationToken observedToken = default;
		TaskCompletionSource<WebMonitorTestResult> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (_, token) =>
			{
				calls++;
				observedToken = token;
				return pending.Task;
			});
		using var window = CreateWindow(
			vm,
			answers: new Queue<MessageDialogResult>([MessageDialogResult.Yes]));
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>().Title =
			"Dirty while testing";

		FindNamed<Button>(window, "WebMonitorTestButton")
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => calls == 1);
		window.FindControl<Button>("RevertButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => !section.IsDirty);
		await DrainUiFourTimesAsync();

		observedToken.IsCancellationRequested.ShouldBeTrue();
		section.IsTestInProgress.ShouldBeFalse();
		section.TestResultMessage.ShouldBeNull();
		pending.SetResult(new WebMonitorTestResult(
			UrlMatched: true,
			Activity: true,
			Revision: "stale",
			Error: null));
		await DrainUiFourTimesAsync();
		section.TestResultMessage.ShouldBeNull();
		section.IsDirty.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_window_close_cancels_test_and_ignores_stale_completion()
	{
		var calls = 0;
		CancellationToken observedToken = default;
		TaskCompletionSource<WebMonitorTestResult> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		(var vm, _) = await CreateViewModelAsync(
			testCurrentWebTabAsync: (_, token) =>
			{
				calls++;
				observedToken = token;
				return pending.Task;
			});
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();

		FindNamed<Button>(window, "WebMonitorTestButton")
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => calls == 1);
		window.Close();
		await DrainUiFourTimesAsync();

		observedToken.IsCancellationRequested.ShouldBeTrue();
		window.IsVisible.ShouldBeFalse();
		pending.SetResult(new WebMonitorTestResult(
			UrlMatched: true,
			Activity: true,
			Revision: "stale",
			Error: null));
		await DrainUiFourTimesAsync();
		section.TestResultMessage.ShouldBeNull();
		section.IsDirty.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Web_monitoring_revert_and_raw_json_keep_existing_section_conventions()
	{
		(var vm, _) = await CreateViewModelAsync();
		RecordingExternalLauncher launcher = new();
		Queue<MessageDialogResult> answers = new(
			[MessageDialogResult.No, MessageDialogResult.Yes]);
		using var window = CreateWindow(vm, launcher, answers);
		await window.InitializeAsync();
		window.Show();
		var section = ActivateWebMonitoring(vm, window);
		await DrainUiFourTimesAsync();
		var item =
			section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>();
		var savedTitle = item.Title;
		item.Title = "Unsaved monitoring title";

		(await window.OpenRawJsonAsync()).ShouldBeTrue();
		launcher.OpenedFiles.ShouldBe([section.FilePath]);
		section.IsDirty.ShouldBeTrue();

		var revert = window.FindControl<Button>("RevertButton").ShouldBeOfType<Button>();
		revert.Content.ShouldBe("Revert");
		revert.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await WaitUntilAsync(() => !section.IsDirty);
		await DrainUiFourTimesAsync();

		section.SelectedItem.ShouldBeOfType<WebMonitorRuleItemViewModel>().Title
			.ShouldBe(savedTitle);
		window.Close();
	}

	[AvaloniaTest]
	public async Task Raw_json_launch_failure_is_projected_through_the_settings_reporter()
	{
		(var vm, _) = await CreateViewModelAsync();
		TaskCompletionSource<Exception> projected = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		using var window = CreateWindow(
			vm,
			new ThrowingExternalLauncher(),
			eventTasks: eventTasks,
			reportUserFailureAsync: exception =>
			{
				projected.TrySetResult(exception);
				return Task.CompletedTask;
			});
		await window.InitializeAsync();
		window.Show();

		window.FindControl<Button>("OpenRawJsonButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await eventTasks.CompleteAndDrainAsync();

		(await projected.Task).Message.ShouldBe("launcher failed");
		window.Close();
	}

	[AvaloniaTest]
	public async Task Prompt_and_git_editors_render_both_tab_levels_with_visible_labels_and_actions()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();

		var prompts = vm.Sections.Single(section => section.Section == SettingsSection.PromptTemplates).ShouldBeOfType<PromptTemplatesSectionViewModel>();
		window.SelectSection(prompts);
		window.UpdateLayout();
		await DrainUiFourTimesAsync();
		var promptGroups = window.GetVisualDescendants()
			.OfType<TabControl>()
			.Where(tabs => ReferenceEquals(tabs.ItemsSource, prompts.Groups))
			.ShouldHaveSingleItem();
		window.TryGetResource(
			"SettingsPrimaryTabItemTheme",
			window.ActualThemeVariant,
			out var primaryTabTheme).ShouldBeTrue();
		window.TryGetResource(
			"SettingsSecondaryTabItemTheme",
			window.ActualThemeVariant,
			out var secondaryTabTheme).ShouldBeTrue();
		promptGroups.ItemContainerTheme.ShouldBeSameAs(primaryTabTheme);
		var promptHeader = promptGroups.ItemTemplate!.Build(prompts.Groups[0]).ShouldBeAssignableTo<Control>()!;
		promptHeader.DataContext = prompts.Groups[0];
		var promptHeaderText = promptHeader.ShouldBeOfType<TextBlock>();
		promptHeaderText.Text.ShouldBe(prompts.Groups[0].Label);
		(promptHeaderText.Padding.Left <= 8).ShouldBeTrue();
		(promptHeaderText.Padding.Top <= 4).ShouldBeTrue();
		var promptContent = promptGroups.ContentTemplate!.Build(prompts.SelectedGroup).ShouldBeAssignableTo<Control>()!;
		promptContent.DataContext = prompts.SelectedGroup;
		var promptItems = promptContent.GetVisualDescendants()
			.OfType<TabControl>()
			.Where(tabs => ReferenceEquals(tabs.ItemsSource, prompts.SelectedGroup!.Items))
			.ShouldHaveSingleItem();
		promptItems.ItemContainerTheme.ShouldBeSameAs(secondaryTabTheme);
		promptItems.SelectedItem.ShouldBeSameAs(prompts.SelectedGroup.SelectedItem);
		AssertVisibleButton(promptContent, "AddPrompt", prompts.SelectedGroup);

		var git = vm.Sections.Single(section => section.Section == SettingsSection.GitHelpers).ShouldBeOfType<GitHelpersSectionViewModel>();
		window.TryGetResource("GitHelpersSectionTemplate", window.ActualThemeVariant, out var gitResource).ShouldBeTrue();
		var gitTemplate = gitResource.ShouldBeAssignableTo<IDataTemplate>()!;
		var gitRoot = gitTemplate.Build(git).ShouldBeAssignableTo<Control>()!;
		gitRoot.DataContext = git;
		var gitTopTabs = gitRoot.GetVisualDescendants().OfType<TabControl>().ShouldHaveSingleItem();
		gitTopTabs.Items
			.OfType<TabItem>()
			.ShouldAllBe(tab => ReferenceEquals(tab.Theme, primaryTabTheme));
		var buttonsTab = gitTopTabs.Items[0].ShouldBeOfType<TabItem>();
		buttonsTab.Header.ShouldBeOfType<TextBlock>().Text.ShouldBe("Buttons");
		var buttonsContent = buttonsTab.Content.ShouldBeAssignableTo<Control>()!;
		var commandTabs = buttonsContent.GetVisualDescendants()
			.OfType<TabControl>()
			.Where(tabs => ReferenceEquals(tabs.ItemsSource, git.CommandItems))
			.ShouldHaveSingleItem();
		commandTabs.ItemContainerTheme.ShouldBeSameAs(secondaryTabTheme);
		commandTabs.SelectedItem.ShouldBeSameAs(git.SelectedCommandItem);
		AssertVisibleButton(buttonsContent, "AddGitCommand", git);

		var moveLeft = buttonsContent.GetVisualDescendants()
			.OfType<Button>()
			.Where(button => Equals(button.Tag, "MoveGitCommandLeft"))
			.ShouldHaveSingleItem();
		var moveRight = buttonsContent.GetVisualDescendants()
			.OfType<Button>()
			.Where(button => Equals(button.Tag, "MoveGitCommandRight"))
			.ShouldHaveSingleItem();
		moveLeft.IsEnabled.ShouldBeFalse();
		moveRight.IsEnabled.ShouldBeTrue();
		ToolTip.GetTip(moveLeft).ShouldBe("Move selected command left");
		ToolTip.GetTip(moveRight).ShouldBe("Move selected command right");

		window.Close();
	}

	[AvaloniaTest]
	public async Task Visiting_terminal_template_tabs_does_not_mark_profiles_dirty()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();

		var launch = vm.Sections.Single(section => section.Section == SettingsSection.LaunchProfiles).ShouldBeOfType<LaunchProfilesSectionViewModel>();
		var changes = launch.Items.ToDictionary(
			profile => profile,
			_ => new List<string?>());
		foreach (var profile in launch.Items)
		{
			profile.PropertyChanged += (_, args) => changes[profile].Add(args.PropertyName);
		}

		vm.ActiveSection = launch;
		window.SelectSection(launch);
		await DrainUiFourTimesAsync();
		var tabs = window.GetVisualDescendants()
			.OfType<ListBox>()
			.Where(list => ReferenceEquals(list.ItemsSource, launch.Items))
			.ShouldHaveSingleItem();

		foreach (var profile in launch.Items.Take(3))
		{
			tabs.SelectedItem = profile;
			await DrainUiFourTimesAsync();
		}

		foreach (var profile in launch.Items)
		{
			profile.IsItemDirty.ShouldBeFalse(
				$"{profile.TabHeader}: {string.Join(", ", changes[profile])}");
		}
		launch.IsDirty.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Moving_a_git_command_keeps_its_tab_selected_and_editor_visible()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var resources = CreateWindow(vm);
		await resources.InitializeAsync();
		var git = vm.Sections.Single(section => section.Section == SettingsSection.GitHelpers).ShouldBeOfType<GitHelpersSectionViewModel>();
		resources.TryGetResource(
			"GitHelpersSectionTemplate",
			resources.ActualThemeVariant,
			out var resource).ShouldBeTrue();
		var gitRoot = resource.ShouldBeAssignableTo<IDataTemplate>()!.Build(git).ShouldBeAssignableTo<Control>()!;
		gitRoot.DataContext = git;
		var topTabs = gitRoot.GetVisualDescendants().OfType<TabControl>().ShouldHaveSingleItem();
		var buttonsContent = topTabs.Items[0].ShouldBeOfType<TabItem>()!.Content.ShouldBeAssignableTo<Control>()!;
		buttonsContent.DataContext = git;
		Window host = new() { Width = 900, Height = 700, Content = buttonsContent };
		host.Show();
		await DrainUiFourTimesAsync();
		var selected = git.SelectedCommandItem.ShouldBeAssignableTo<SettingsItemViewModelBase>()!;
		var commandTabs = host.GetVisualDescendants()
			.OfType<TabControl>()
			.Where(tabs => ReferenceEquals(tabs.ItemsSource, git.CommandItems))
			.ShouldHaveSingleItem();

		git.MoveSelectedCommand(1);
		await DrainUiFourTimesAsync();

		git.SelectedCommandItem.ShouldBeSameAs(selected);
		commandTabs.SelectedItem.ShouldBeSameAs(selected);
		commandTabs.IsEffectivelyVisible.ShouldBeTrue();
		host.Close();
	}

	[AvaloniaTest]
	public async Task Scenario_default_reviewer_selection_is_two_way_and_saveable()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		var scenarios = vm.Sections.Single(section => section.Section == SettingsSection.Scenarios).ShouldBeOfType<ScenariosSectionViewModel>();
		window.SelectSection(scenarios);
		await SettingsWindow.DrainUiQueueAsync();

		var scenario = scenarios.SelectedItem.ShouldBeOfType<ScenarioItemViewModel>();
		var selector = window.GetVisualDescendants()
			.OfType<ComboBox>()
			.Where(combo => ReferenceEquals(combo.ItemsSource, scenario.ReviewerInstructions))
			.ShouldHaveSingleItem();
		selector.SelectedItem.ShouldBeSameAs(scenario.DefaultReviewerInstruction);

		var replacement = scenario.ReviewerInstructions[^1];
		selector.SelectedItem = replacement;
		await SettingsWindow.DrainUiQueueAsync();

		scenario.DefaultReviewerInstructionId.ShouldBe(replacement.Id);
		(await scenarios.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Large_settings_editor_keeps_footer_inside_the_window()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();
		window.SelectSection(vm.Sections.Single(section => section.Section == SettingsSection.Scenarios));
		await SettingsWindow.DrainUiQueueAsync();

		var save = window.FindControl<Button>("SaveButton").ShouldBeOfType<Button>();
		save.IsEffectivelyVisible.ShouldBeTrue();
		var savePosition = save.TranslatePoint(default, window).ShouldBeOfType<Point>();
		savePosition.Y.ShouldBeInRange(0, window.ClientSize.Height - save.Bounds.Height);

		window.Close();
	}

	[AvaloniaTest]
	public async Task Dirty_section_switch_can_cancel_or_discard_and_reload()
	{
		(var vm, _) = await CreateViewModelAsync();
		Queue<MessageDialogResult> answers = new([MessageDialogResult.No, MessageDialogResult.Yes]);
		using var window = CreateWindow(vm, answers: answers);
		await window.InitializeAsync();

		var launch = vm.Sections.Single(section => section.Section == SettingsSection.LaunchProfiles).ShouldBeOfType<LaunchProfilesSectionViewModel>();
		var first = launch.Items[0].ShouldBeOfType<ShellProfileItemViewModel>();
		var savedName = first.DisplayName;
		vm.ActiveSection = launch;
		window.SelectSection(launch);
		first.DisplayName = "Dirty name";

		var target = vm.Sections.Single(section => section.Section == SettingsSection.WebLinkTemplates);
		(await window.TrySelectSectionAsync(target)).ShouldBeFalse();
		vm.ActiveSection.ShouldBeSameAs(launch);
		first.DisplayName.ShouldBe("Dirty name");

		(await window.TrySelectSectionAsync(target)).ShouldBeTrue();
		vm.ActiveSection.ShouldBeSameAs(target);
		launch.IsDirty.ShouldBeFalse();
		launch.Items[0].ShouldBeOfType<ShellProfileItemViewModel>().DisplayName.ShouldBe(savedName);
	}

	[AvaloniaTest]
	public async Task Open_raw_json_honors_cancel_no_and_yes_without_losing_edits()
	{
		(var vm, _) = await CreateViewModelAsync();
		RecordingExternalLauncher launcher = new();
		Queue<MessageDialogResult> answers = new(
			[MessageDialogResult.Cancel, MessageDialogResult.No, MessageDialogResult.Yes]);
		using var window = CreateWindow(vm, launcher, answers);
		await window.InitializeAsync();

		var launch = ActivateLaunchProfiles(vm, window);
		var first = launch.Items[0].ShouldBeOfType<ShellProfileItemViewModel>();
		first.DisplayName = "Keep this edit";

		(await window.OpenRawJsonAsync()).ShouldBeFalse();
		launcher.OpenedFiles.ShouldBeEmpty();
		launch.IsDirty.ShouldBeTrue();

		(await window.OpenRawJsonAsync()).ShouldBeTrue();
		launcher.OpenedFiles.ShouldBe([launch.FilePath]);
		launch.IsDirty.ShouldBeTrue();

		(await window.OpenRawJsonAsync()).ShouldBeTrue();
		launcher.OpenedFiles.ShouldBe([launch.FilePath, launch.FilePath]);
		launch.IsDirty.ShouldBeFalse();
		window.SavedAnyFile.ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task Ctrl_s_saves_active_section_and_revert_tracks_dirty_state()
	{
		(var vm, _) = await CreateViewModelAsync();
		using var window = CreateWindow(vm);
		await window.InitializeAsync();
		window.Show();

		var launch = ActivateLaunchProfiles(vm, window);
		window.FindControl<Button>("RevertButton")!.IsEnabled.ShouldBeFalse();
		launch.Items[0].ShouldBeOfType<ShellProfileItemViewModel>().DisplayName = "Saved by shortcut";
		window.FindControl<Button>("RevertButton")!.IsEnabled.ShouldBeTrue();

		KeyEventArgs key = new()
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.S,
			KeyModifiers = KeyModifiers.Control
		};
		window.RaiseEvent(key);
		await SettingsWindow.DrainUiQueueAsync();
		await WaitUntilAsync(() => !launch.IsDirty);

		key.Handled.ShouldBeTrue();
		launch.IsDirty.ShouldBeFalse();
		window.SavedAnyFile.ShouldBeTrue();
		window.FindControl<Button>("RevertButton")!.IsEnabled.ShouldBeFalse();
		window.Close();
	}

	[AvaloniaTest]
	public async Task Escape_uses_the_close_discard_path()
	{
		(var vm, _) = await CreateViewModelAsync();
		Queue<MessageDialogResult> answers = new([MessageDialogResult.No, MessageDialogResult.Yes]);
		using var window = CreateWindow(vm, answers: answers);
		await window.InitializeAsync();
		window.Show();
		var launch = ActivateLaunchProfiles(vm, window);
		launch.Items[0].ShouldBeOfType<ShellProfileItemViewModel>().DisplayName = "Unsaved";

		RaiseEscape(window);
		await DrainUiTwiceAsync();
		window.IsVisible.ShouldBeTrue();

		RaiseEscape(window);
		await DrainUiTwiceAsync();
		window.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Delete_uses_injected_owned_confirmation()
	{
		(var vm, _) = await CreateViewModelAsync();
		Queue<MessageDialogResult> answers = new([MessageDialogResult.No, MessageDialogResult.Yes]);
		using var window = CreateWindow(vm, answers: answers);
		await window.InitializeAsync();
		var launch = ActivateLaunchProfiles(vm, window);
		var item = launch.Items[0];
		var before = launch.Items.Count;

		(await window.TryDeleteItemAsync(item)).ShouldBeFalse();
		launch.Items.Count.ShouldBe(before);
		(await window.TryDeleteItemAsync(item)).ShouldBeTrue();
		launch.Items.Count.ShouldBe(before - 1);
	}

	private async Task<(SettingsWindowViewModel ViewModel, WorkspaceViewModel Workspace)> CreateViewModelAsync(
		bool includeSession = false,
		Func<WebMonitorRule, CancellationToken, Task<WebMonitorTestResult>>?
			testCurrentWebTabAsync = null)
	{
		SettingsFileStore store = new(_root);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var projectRoot = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(
			new ProjectRecord("project-1", "Project one", projectRoot, now, now, null),
			_ => false);
		if (includeSession)
		{
			workspace.Sessions.Add(new SessionViewModel(new SessionRecord(
				"session-1", AgentKind.Codex, "Codex session", projectRoot, "codex", null,
				SessionStatus.Running, now, now)));
		}

		SettingsWindowViewModel vm = new(
			store,
			() => [workspace],
			new FakeProjectSettingsEditor(),
			() => Task.FromResult<string?>(null),
			testCurrentWebTabAsync: testCurrentWebTabAsync);
		return (vm, workspace);
	}

	private static SettingsWindow CreateWindow(
		SettingsWindowViewModel vm,
		IExternalLauncher? launcher = null,
		Queue<MessageDialogResult>? answers = null,
		ObservedTaskGroup? eventTasks = null,
		Func<Exception, Task>? reportUserFailureAsync = null) => new SettingsWindow(
			vm,
			launcher ?? new RecordingExternalLauncher(),
			request => Task.FromResult(answers?.Dequeue() ?? request.DefaultResult),
			eventTasks: eventTasks,
			reportUserFailureAsync: reportUserFailureAsync);

	private static LaunchProfilesSectionViewModel ActivateLaunchProfiles(
		SettingsWindowViewModel vm,
		SettingsWindow window)
	{
		var launch = vm.Sections.Single(section => section.Section == SettingsSection.LaunchProfiles).ShouldBeOfType<LaunchProfilesSectionViewModel>();
		vm.ActiveSection = launch;
		window.SelectSection(launch);
		return launch;
	}

	private static WebMonitoringRulesSectionViewModel ActivateWebMonitoring(
		SettingsWindowViewModel vm,
		SettingsWindow window)
	{
		var monitoring = vm.Sections
			.Single(section => section.Section == SettingsSection.WebMonitoringRules)
			.ShouldBeOfType<WebMonitoringRulesSectionViewModel>();
		vm.ActiveSection = monitoring;
		window.SelectSection(monitoring);
		return monitoring;
	}

	private static TControl FindNamed<TControl>(Control root, string name)
		where TControl : Control => root.GetVisualDescendants()
			.OfType<TControl>()
			.Where(control => string.Equals(control.Name, name, StringComparison.Ordinal))
			.ShouldHaveSingleItem();

	private static void RaiseEscape(SettingsWindow window) => window.RaiseEvent(new KeyEventArgs
	{
		RoutedEvent = InputElement.KeyDownEvent,
		Key = Key.Escape
	});

	private static void AssertTabEditor(Control root, object itemsSource)
	{
		var tabs = root.GetVisualDescendants()
			.OfType<ListBox>()
			.Where(control => ReferenceEquals(control.ItemsSource, itemsSource))
			.ShouldHaveSingleItem();
		tabs.IsEffectivelyVisible.ShouldBeTrue();
		ScrollViewer.GetHorizontalScrollBarVisibility(tabs).ShouldBe(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
		ScrollViewer.GetVerticalScrollBarVisibility(tabs).ShouldBe(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
		var panel = tabs.ItemsPanel!.Build().ShouldBeOfType<StackPanel>();
		panel.Orientation.ShouldBe(Orientation.Horizontal);
		tabs.ItemContainerTheme.ShouldNotBeNull();
		var first = itemsSource.ShouldBeAssignableTo<System.Collections.IEnumerable>()!.Cast<object>().First();
		var header = tabs.ItemTemplate!.Build(first).ShouldBeAssignableTo<Control>()!;
		header.DataContext = first;
		string.IsNullOrWhiteSpace(header.ShouldBeOfType<TextBlock>().Text).ShouldBeFalse();
	}

	private static void AssertVisibleButton(Control root, string tag, object dataContext)
	{
		var button = root.GetVisualDescendants()
			.OfType<Button>()
			.Where(candidate =>
				Equals(candidate.Tag, tag)
				&& ReferenceEquals(candidate.DataContext, dataContext))
			.ShouldHaveSingleItem();
		button.IsEffectivelyVisible.ShouldBeTrue();
	}

	private static async Task DrainUiTwiceAsync()
	{
		await SettingsWindow.DrainUiQueueAsync();
		await SettingsWindow.DrainUiQueueAsync();
	}

	private static async Task DrainUiFourTimesAsync()
	{
		await DrainUiTwiceAsync();
		await DrainUiTwiceAsync();
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
		while (!condition())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private sealed class RecordingExternalLauncher : IExternalLauncher
	{
		public List<string> OpenedFiles { get; } = [];

		public Task OpenFileAsync(string path)
		{
			OpenedFiles.Add(path);
			return Task.CompletedTask;
		}

		public Task OpenHttpUriAsync(Uri uri) => Task.CompletedTask;
	}

	private sealed class ThrowingExternalLauncher : IExternalLauncher
	{
		public Task OpenFileAsync(string path) =>
			Task.FromException(new IOException("launcher failed"));

		public Task OpenHttpUriAsync(Uri uri) =>
			Task.FromException(new IOException("launcher failed"));
	}

	private sealed class FakeProjectSettingsEditor : IProjectSettingsEditor
	{
		public Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct) =>
			Task.CompletedTask;

		public Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct) =>
			Task.CompletedTask;

		public Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct) =>
			Task.FromResult<string?>(null);
	}
}

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.App.Avalonia.Views.Settings;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class SettingsWindowHeadlessTests
{
	[AvaloniaTest]
	public void Window_materializes_complete_settings_shell()
	{
		using SettingsWindow window = new();

		window.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
		window.FindControl<ListBox>("SectionList").ShouldNotBeNull();
		window.FindControl<ContentControl>("SectionContent").ShouldNotBeNull();
		window.FindControl<Button>("SectionHelpButton").ShouldNotBeNull();
		window.FindControl<Button>("OpenRawJsonButton").ShouldNotBeNull();
		window.FindControl<Button>("RevertButton").ShouldNotBeNull();
		window.FindControl<Button>("SaveButton").ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void Window_loads_templates_for_all_eight_section_types()
	{
		using SettingsWindow window = new();
		string[] keys =
		[
			"ProjectsSectionTemplate",
			"PausedProjectsSectionTemplate",
			"LaunchProfilesSectionTemplate",
			"WebLinkTemplatesSectionTemplate",
			"PromptTemplatesSectionTemplate",
			"GitHelpersSectionTemplate",
			"ScenariosSectionTemplate",
			"RecentDirectoriesSectionTemplate"
		];

		foreach (var key in keys)
		{
			window.TryGetResource(key, window.ActualThemeVariant, out var resource).ShouldBeTrue();
			resource.ShouldBeAssignableTo<IDataTemplate>();
		}
	}

	[AvaloniaTest]
	public void AppearanceTemplateOffersSelectedTabDetailsAndExternalMetricsToggles()
	{
		using SettingsWindow window = new();
		window.TryGetResource(
			"AppearanceSectionTemplate",
			window.ActualThemeVariant,
			out var resource).ShouldBeTrue();
		var template = resource.ShouldBeAssignableTo<IDataTemplate>()!;

		var root = template.Build(null).ShouldBeAssignableTo<Control>()!;

		root.GetSelfAndVisualDescendants().OfType<CheckBox>()
			.Select(checkBox => checkBox.Content)
			.ShouldBe([
				"Show selected tab details",
				"Show external process metrics"
			]);
	}

	[AvaloniaTest]
	public void Secondary_window_palette_has_distinct_light_and_dark_surfaces()
	{
		App app = new();
		app.Initialize();
		app.TryGetResource("AppPaneBackgroundBrush", ThemeVariant.Light, out var lightPane).ShouldBeTrue();
		app.TryGetResource("AppPaneBackgroundBrush", ThemeVariant.Dark, out var darkPane).ShouldBeTrue();
		app.TryGetResource("AppSurfaceBrush", ThemeVariant.Light, out var lightSurface).ShouldBeTrue();
		app.TryGetResource("AppSurfaceBrush", ThemeVariant.Dark, out var darkSurface).ShouldBeTrue();
		app.TryGetResource("AppTextPrimaryBrush", ThemeVariant.Light, out var lightText).ShouldBeTrue();
		app.TryGetResource("AppTextPrimaryBrush", ThemeVariant.Dark, out var darkText).ShouldBeTrue();

		lightPane.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#F8FAFC"));
		darkPane.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#0F172A"));
		lightSurface.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#FFFFFF"));
		darkSurface.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#111827"));
		lightText.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#111827"));
		darkText.ShouldBeOfType<SolidColorBrush>().Color.ShouldBe(Color.Parse("#F8FAFC"));
	}

	[AvaloniaTest]
	public async Task Settings_tab_themes_share_typography_and_selected_indicator()
	{
		using SettingsWindow resources = new();
		resources.TryGetResource(
			"SettingsTabItemTheme",
			resources.ActualThemeVariant,
			out var listResource).ShouldBeTrue();
		resources.TryGetResource(
			"SettingsPrimaryTabItemTheme",
			resources.ActualThemeVariant,
			out var primaryResource).ShouldBeTrue();
		resources.TryGetResource(
			"SettingsSecondaryTabItemTheme",
			resources.ActualThemeVariant,
			out var secondaryResource).ShouldBeTrue();

		var listTheme = listResource.ShouldBeOfType<ControlTheme>();
		var primaryTheme = primaryResource.ShouldBeOfType<ControlTheme>();
		var secondaryTheme = secondaryResource.ShouldBeOfType<ControlTheme>();
		ListBoxItem listItem = new()
		{
			Theme = listTheme,
			Content = "Web link"
		};
		TabItem primaryItem = new()
		{
			Theme = primaryTheme,
			Content = "Prompts",
			IsSelected = true
		};
		TabItem secondaryItem = new()
		{
			Theme = secondaryTheme,
			Content = "Review git diff",
			IsSelected = true
		};
		Window host = new()
		{
			Width = 800,
			Height = 400,
			RequestedThemeVariant = ThemeVariant.Light,
			Content = new StackPanel
			{
				Children =
				{
					listItem,
					primaryItem,
					secondaryItem
				}
			}
		};
		host.Show();
		host.UpdateLayout();
		await SettingsWindow.DrainUiQueueAsync();
		await SettingsWindow.DrainUiQueueAsync();
		await SettingsWindow.DrainUiQueueAsync();
		await SettingsWindow.DrainUiQueueAsync();

		listItem.FontFamily.ToString().ShouldBe("Segoe UI");
		primaryItem.FontFamily.ShouldBe(listItem.FontFamily);
		secondaryItem.FontFamily.ShouldBe(listItem.FontFamily);
		listItem.FontSize.ShouldBe(14);
		primaryItem.FontSize.ShouldBe(15);
		secondaryItem.FontSize.ShouldBe(14);

		var primaryBorder = primaryItem.GetVisualDescendants().OfType<Border>().ShouldHaveSingleItem();
		var secondaryBorder = secondaryItem.GetVisualDescendants().OfType<Border>().ShouldHaveSingleItem();
		primaryBorder.BorderThickness.Bottom.ShouldBe(2);
		secondaryBorder.BorderThickness.Bottom.ShouldBe(2);
		host.Close();
	}

	[AvaloniaTest]
	public async Task Settings_tab_theme_renders_the_header_instead_of_the_editor_content()
	{
		using SettingsWindow resources = new();
		resources.TryGetResource(
			"SettingsPrimaryTabItemTheme",
			resources.ActualThemeVariant,
			out var themeResource).ShouldBeTrue();
		TextBox editor = new() { Text = "Editor content" };
		TabItem tab = new()
		{
			Theme = themeResource.ShouldBeOfType<ControlTheme>(),
			Header = "Buttons",
			Content = editor
		};
		Window host = new()
		{
			Width = 500,
			Height = 200,
			RequestedThemeVariant = ThemeVariant.Light,
			Content = tab
		};
		host.Show();
		await SettingsWindow.DrainUiQueueAsync();

		var presenter = tab.GetVisualDescendants().OfType<ContentPresenter>().ShouldHaveSingleItem();
		presenter.Content.ShouldBe("Buttons");
		tab.GetVisualDescendants().ShouldNotContain(editor);
		host.Close();
	}

	[AvaloniaTest]
	public void Prompt_body_editor_wraps_and_has_no_horizontal_scrollbar()
	{
		using SettingsWindow window = new();
		window.TryGetResource(
			"PromptTemplateItemTemplate",
			window.ActualThemeVariant,
			out var resource).ShouldBeTrue();
		var template = resource.ShouldBeAssignableTo<IDataTemplate>()!;
		PromptTemplateItemViewModel item = new(new JsonObject
		{
			["id"] = "prompt",
			["name"] = "Prompt",
			["body"] = "line one\nline two"
		});
		var root = template.Build(item).ShouldBeAssignableTo<Control>()!;
		root.DataContext = item;
		var body = root.GetSelfAndVisualDescendants().OfType<TextBox>().Where(box => box.AcceptsReturn).ShouldHaveSingleItem();

		body.TextWrapping.ShouldBe(TextWrapping.Wrap);
		ScrollViewer.GetHorizontalScrollBarVisibility(body).ShouldBe(global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
	}

	[AvaloniaTest]
	public void Project_editor_reserves_space_for_its_vertical_scrollbar()
	{
		using SettingsWindow window = new();
		window.TryGetResource(
			"ProjectItemTemplate",
			window.ActualThemeVariant,
			out var resource).ShouldBeTrue();
		var template = resource.ShouldBeAssignableTo<IDataTemplate>()!;
		Presentation.ViewModels.WorkspaceViewModel workspace = new(
			new Core.Projects.ProjectRecord(
				"project", "Project", Environment.CurrentDirectory,
				DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null),
			_ => false);
		ProjectItemViewModel item = new(workspace);
		var scroll = template.Build(item).ShouldBeOfType<ScrollViewer>()!;
		var editor = scroll.Content.ShouldBeOfType<StackPanel>()!;

		(editor.Margin.Right >= 20).ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task Confirmation_dialog_keeps_all_visible_actions_inside_its_client_area()
	{
		MessageDialogWindow window = new(new MessageDialogRequest(
			"Unsaved changes",
			"Discard unsaved settings changes?",
			MessageDialogButtons.YesNoCancel,
			MessageDialogResult.Cancel));

		window.Show();
		await SettingsWindow.DrainUiQueueAsync();

		window.RequestedThemeVariant.ShouldBe(ThemeVariant.Default);
		foreach (var name in new[] { "YesButton", "NoButton", "CancelButton" })
		{
			var button = window.FindControl<Button>(name).ShouldBeOfType<Button>();
			button.IsEffectivelyVisible.ShouldBeTrue();
			var point = button.TranslatePoint(default, window).ShouldBeOfType<Point>();
			point.X.ShouldBeInRange(0, window.ClientSize.Width - button.Bounds.Width);
			point.Y.ShouldBeInRange(0, window.ClientSize.Height - button.Bounds.Height);
		}

		window.Close();
	}
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed partial class SkillCatalogWindowTests
{
	[AvaloniaTest]
	public Task Malformed_source_settings_remain_visible_and_cannot_be_overwritten_from_either_tab() =>
		AssertSourceSettingsReadFailureAsync("{\"sources\":[\"existing\"],\"mappingFolders\":17}");

	[AvaloniaTest]
	public Task Duplicate_source_settings_keys_disable_writes_and_leave_installed_skills_visible() =>
		AssertSourceSettingsReadFailureAsync("{\"sources\":[],\"sources\":[\"existing\"]}");

	private static async Task AssertSourceSettingsReadFailureAsync(string malformed)
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		Directory.CreateDirectory(paths.SettingsDirectory);
		var settings = Path.Combine(paths.SettingsDirectory, "skill-sources.json");
		await File.WriteAllTextAsync(settings, malformed);
		var installed = Path.Combine(temp.Path, "installed");
		var package = Directory.CreateDirectory(Path.Combine(installed, "available")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Available");
		using var window = new SkillCatalogWindow(paths, [], [temp.Path], temp.Path, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var details = window.FindControl<TabControl>("Details")!;
			details.SelectedItem.ShouldBe(window.FindControl<TabItem>("AvailabilityTab"));
			var status = window.FindControl<SelectableTextBlock>("LoadStatus")!;
			status.Text.ShouldNotBeNull().ShouldContain(settings);
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			host.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "MatrixPickFolder").IsEnabled.ShouldBeFalse();
			Assert.ThrowsAsync<InvalidOperationException>(() => window.AddMappingDirectoryAsync(temp.Path, false));
			details.SelectedIndex = 0;
			window.FindControl<Expander>("SourceEditor")!.IsEnabled.ShouldBeFalse();
			window.FindControl<TextBlock>("SkillTitle")!.Text.ShouldBe("available");
			status.Text.ShouldNotBeNull().ShouldContain(settings);
			(await File.ReadAllTextAsync(settings)).ShouldBe(malformed);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Installation_preview_reports_malformed_codex_settings_alongside_other_profiles()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		Directory.CreateDirectory(paths.SettingsDirectory);
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source", "review")).Parent!.FullName;
		await File.WriteAllTextAsync(Path.Combine(source, "review", "SKILL.md"), "# Review");
		await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "skill-sources.json"),
			System.Text.Json.JsonSerializer.Serialize(new { sources = new[] { source } }));
		var brokenHome = Directory.CreateDirectory(Path.Combine(temp.Path, "broken")).FullName;
		await File.WriteAllTextAsync(Path.Combine(brokenHome, "config.toml"), "[[skills.config]\n");
		var broken = new AgentProfileRecord("broken", AgentKind.Codex, "Broken Codex", "codex", null, "pwsh")
		{ NativeConfigDirectory = brokenHome };
		var other = new AgentProfileRecord("other", AgentKind.Codex, "Other Codex", "codex", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "other") };
		using var window = new SkillCatalogWindow(paths, [broken, other], [], null, Path.Combine(temp.Path, "shared"));
		try
		{
			await AfterOperationAsync(window, window.Show);
			var tree = window.FindControl<TreeView>("Skills")!;
			tree.SelectedItem = tree.Items.OfType<TreeViewItem>().SelectMany(node => node.Items.OfType<TreeViewItem>())
				.Single(item => Equals(item.Header, "review"));
			await ClickAsync(window, "Install");
			var preview = window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull();
			preview.ShouldContain("Broken Codex · profile default:");
			preview.ShouldContain("Other Codex (other) · profile default");
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeTrue();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Private_skill_and_group_have_no_foreign_profile_choices_until_made_shared()
	{
		using var temp = TemporaryDirectory.Create();
		var owner = new AgentProfileRecord("work", AgentKind.Claude, "Work", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "work") };
		var other = new AgentProfileRecord("second", AgentKind.Codex, "Second", "codex", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "second") };
		var root = Path.Combine(owner.NativeConfigDirectory, "skills");
		var package = Directory.CreateDirectory(Path.Combine(root, "profile-only")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Profile-only");
		var shared = Path.Combine(temp.Path, "shared");
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [owner, other], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var tabs = window.FindControl<TabControl>("Details")!;
			var catalogTab = tabs.SelectedItem;
			var availability = window.FindControl<TabItem>("AvailabilityTab")!;
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			async Task Expand(string location)
			{
				tabs.SelectedItem = availability;
				await window.MatrixReady;
				var expand = host.GetLogicalDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), location));
				expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			}
			IEnumerable<string?> Choices()
			{
				return host.GetLogicalDescendants().OfType<ToggleButton>().Select(AutomationProperties.GetName);
			}
			await Expand(root);
			Choices().ShouldContain("On: profile-only for work");
			Choices().ShouldNotContain("On: profile-only for second");
			Choices().ShouldNotContain(name => name != null && name.Contains("All skills in", StringComparison.Ordinal)
				&& name.EndsWith("for second", StringComparison.Ordinal));
			tabs.SelectedItem = catalogTab;
			await ClickAsync(window, "MakeShared");
			await ClickAsync(window, "Apply");
			await Expand(shared);
			Choices().ShouldContain("On: profile-only for work");
			Choices().ShouldContain("On: profile-only for second");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Project_source_policy_preserves_an_individual_exception_through_preview_and_apply()
	{
		using var temp = TemporaryDirectory.Create();
		var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
		var installed = Path.Combine(temp.Path, "installed");
		var package = Directory.CreateDirectory(Path.Combine(installed, "local-review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Test skill");
		var second = Directory.CreateDirectory(Path.Combine(installed, "review-two")).FullName;
		await File.WriteAllTextAsync(Path.Combine(second, "SKILL.md"), "# Second skill");
		AgentProfileRecord profile = new("claude-test", AgentKind.Claude, "Claude", "claude", null, "pwsh")
		{
			NativeConfigDirectory = Path.Combine(temp.Path, "claude")
		};
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")),
			[profile], [project], project, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			window.FindControl<TabControl>("Details")!.SelectedItem.ShouldBe(window.FindControl<TabItem>("AvailabilityTab"));
			await window.MatrixReady;
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			TControl Named<TControl>(string name) where TControl : Control
			{
				return host.GetLogicalDescendants().OfType<TControl>().Single(c => c.Name == name);
			}
			ToggleButton Choice(string name)
			{
				return host.GetLogicalDescendants().OfType<ToggleButton>().Single(c => AutomationProperties.GetName(c) == name);
			}
			var group = host.GetLogicalDescendants().OfType<ToggleButton>()
				.Single(c => AutomationProperties.GetName(c)?.StartsWith("Off: All skills in ", StringComparison.Ordinal) == true);
			var groupName = AutomationProperties.GetName(group)!;
			Named<Button>("MatrixApply").IsVisible.ShouldBeFalse();
			group.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			var expand = host.GetLogicalDescendants().OfType<Button>()
				.Single(b => AutomationProperties.GetName(b)?.StartsWith("Expand or collapse ", StringComparison.Ordinal) == true);
			expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Choice("On: local-review for claude-test").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Choice("Inherit: review-two for claude-test").IsChecked.ShouldBe(true);
			Choice(groupName).IsChecked.ShouldBe(true);
			host.GetLogicalDescendants().OfType<TextBlock>().ShouldContain(b => b.Text == "Off · from group");
			await window.MatrixImpactReady;
			var message = Named<SelectableTextBlock>("MatrixSummary").Text.ShouldNotBeNull();
			message.ShouldContain("Profiles: Claude");
			message.ShouldContain(Path.Combine(project, ".claude", "skills", "local-review"));
			message.ShouldContain(Path.Combine(temp.Path, "pact", "Settings", "Skills", "Connections"));
			Named<Button>("MatrixApply").IsVisible.ShouldBeTrue();
			Directory.Exists(Path.Combine(project, ".claude")).ShouldBeFalse();
			await AfterOperationAsync(window, () => Named<Button>("MatrixApply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			await window.MatrixReady;
			Choice(groupName).IsChecked.ShouldBe(true);
			Choice("On: local-review for claude-test").IsChecked.ShouldBe(true);
			Choice("Inherit: review-two for claude-test").IsChecked.ShouldBe(true);
			Directory.Exists(Path.Combine(project, ".claude", "skills", "local-review")).ShouldBeTrue();
			Named<Button>("MatrixApply").IsVisible.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Switching_to_a_project_keeps_pending_profile_edits_separate_until_discarded()
	{
		using var temp = TemporaryDirectory.Create();
		var native = Path.Combine(temp.Path, "claude");
		var package = Directory.CreateDirectory(Path.Combine(native, "skills", "native-review"));
		await File.WriteAllTextAsync(Path.Combine(package.FullName, "SKILL.md"), "# Review");
		var project = Directory.CreateDirectory(Path.Combine(temp.Path, "project")).FullName;
		var profile = new AgentProfileRecord("work", AgentKind.Claude, "Work", "claude", null, "pwsh") { NativeConfigDirectory = native };
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")),
			[profile], [project], null, Path.Combine(temp.Path, "installed"), profile.Id);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await window.MatrixReady;
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			ToggleButton SourceChoice(string value)
			{
				return host.GetLogicalDescendants().OfType<ToggleButton>()
					.Single(c => AutomationProperties.GetName(c)?.StartsWith(value + ": All skills in ", StringComparison.Ordinal) == true);
			}
			Button Action(string name)
			{
				return host.GetLogicalDescendants().OfType<Button>().Single(c => c.Name == name);
			}
			SourceChoice("Off").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			var scope = host.GetLogicalDescendants().OfType<TreeView>().Single(c => c.Name == "MatrixScope");
			scope.SelectedItem = MappingNode(scope, $"Project · {project}");
			await window.MatrixReady;
			SourceChoice("Inherit").IsChecked.ShouldBe(true);
			scope.SelectedItem = scope.Items[0];
			await window.MatrixReady;
			SourceChoice("Off").IsChecked.ShouldBe(true);
			await window.MatrixImpactReady;
			Action("MatrixApply").IsVisible.ShouldBeTrue();
			Action("MatrixDiscard").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			SourceChoice("Inherit").IsChecked.ShouldBe(true);
			Action("MatrixApply").IsVisible.ShouldBeFalse();
			File.Exists(Path.Combine(native, "settings.json")).ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Folder_mapping_is_saved_independently_of_the_project_at_the_same_path()
	{
		using var temp = TemporaryDirectory.Create();
		var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "workspace")).FullName;
		var shared = Path.Combine(temp.Path, "shared");
		var package = Directory.CreateDirectory(Path.Combine(shared, "check")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Check");
		var profile = new AgentProfileRecord("work", AgentKind.Claude, "Work", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "claude") };
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		using var window = new SkillCatalogWindow(paths, [profile], [folder], folder.ToUpperInvariant(), shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await window.MatrixReady;
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			var scope = host.GetLogicalDescendants().OfType<TreeView>().Single(control => control.Name == "MatrixScope");
			var project = MappingNode(scope, $"Project · {folder}");
			scope.SelectedItem.ShouldBeSameAs(project);
			ToggleButton Choice(string mode)
			{
				return host.GetLogicalDescendants().OfType<ToggleButton>().Single(button =>
					AutomationProperties.GetName(button)?.StartsWith(mode + ": All skills in ", StringComparison.Ordinal) == true);
			}
			await window.AddMappingDirectoryAsync(folder, true);
			await window.MatrixReady;
			var root = MappingNode(scope, $"Folder · {folder}");
			scope.SelectedItem.ShouldBeSameAs(root);
			root.Items.ShouldContain(project);
			Choice("Off").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await window.MatrixImpactReady;
			var summary = host.GetLogicalDescendants().OfType<SelectableTextBlock>().Single(control => control.Name == "MatrixSummary");
			summary.Text.ShouldNotBeNull().ShouldContain($"Folder · {folder}");
			var apply = host.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "MatrixApply");
			apply.IsVisible.ShouldBeTrue();
			await AfterOperationAsync(window, () => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			await window.MatrixReady;
			Choice("Off").IsChecked.ShouldBe(true);
			scope.SelectedItem = project;
			await window.MatrixReady;
			scope.SelectedItem.ShouldBeSameAs(project);
			Choice("Inherit").IsChecked.ShouldBe(true);
			var expand = host.GetLogicalDescendants().OfType<Button>()
				.Single(b => AutomationProperties.GetName(b)?.StartsWith("Expand or collapse ", StringComparison.Ordinal) == true);
			expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			host.GetLogicalDescendants().OfType<TextBlock>().ShouldContain(block =>
				block.Text != null && block.Text.StartsWith("Off", StringComparison.Ordinal) && block.Text.Contains(folder, StringComparison.Ordinal));
			var unopened = Directory.CreateDirectory(Path.Combine(folder, "new-project")).FullName;
			await window.AddMappingDirectoryAsync(unopened, false);
			await window.MatrixReady;
			scope.SelectedItem.ShouldBeSameAs(MappingNode(root, $"Project · {unopened}"));
		}
		finally
		{
			window.Close();
		}
		using var reopened = new SkillCatalogWindow(paths, [profile], [], null, shared, profile.Id);
		try
		{
			await AfterOperationAsync(reopened, reopened.Show);
			await reopened.MatrixReady;
			var scopes = reopened.FindControl<ContentControl>("MatrixHost")!.GetLogicalDescendants().OfType<TreeView>().Single();
			var root = MappingNode(scopes, $"Folder · {folder}");
			MappingNode(root, $"Project · {Path.Combine(folder, "new-project")}").ShouldNotBeNull();
		}
		finally
		{
			reopened.Close();
		}
	}

	private static TreeViewItem MappingNode(ItemsControl parent, string name)
	{
		return parent.GetLogicalDescendants().OfType<TreeViewItem>().Single(node => AutomationProperties.GetName(node) == name);
	}

	[AvaloniaTest]
	public async Task Registered_source_can_be_disabled_before_its_first_installation()
	{
		using var temp = TemporaryDirectory.Create();
		var source = Path.Combine(temp.Path, "team");
		var package = Directory.CreateDirectory(Path.Combine(source, "pending")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Pending");
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "skill-sources.json"),
			System.Text.Json.JsonSerializer.Serialize(new { sources = new[] { source } }));
		var installed = Path.Combine(temp.Path, "installed");
		var profile = new AgentProfileRecord("work", AgentKind.Claude, "Work", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "claude") };
		using var window = new SkillCatalogWindow(paths, [profile], [], null, installed, profile.Id);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await window.MatrixReady;
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			ToggleButton GroupOff()
			{
				return host.GetLogicalDescendants().OfType<ToggleButton>()
					.Single(b => AutomationProperties.GetName(b)?.StartsWith("Off: All skills in ", StringComparison.Ordinal) == true);
			}
			var expand = host.GetLogicalDescendants().OfType<Button>()
				.Single(b => AutomationProperties.GetName(b)?.StartsWith("Expand or collapse ", StringComparison.Ordinal) == true);
			expand.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			host.GetLogicalDescendants().OfType<TextBlock>().ShouldContain(b => b.Text == "Install from Catalog");
			GroupOff().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await window.MatrixImpactReady;
			var apply = host.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "MatrixApply");
			apply.IsVisible.ShouldBeTrue();
			await AfterOperationAsync(window, () => apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			await window.MatrixReady;
			GroupOff().IsChecked.ShouldBe(true);
			Directory.Exists(Path.Combine(installed, "pending")).ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task PreviewShowsExactUpdateAndRequiresApplyBeforeChangingInstalledFile()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var native = Path.Combine(temp.Path, "native");
		var source = Path.Combine(temp.Path, "team");
		var folder = Directory.CreateDirectory(Path.Combine(source, "review"));
		var manifest = Path.Combine(folder.FullName, "SKILL.md");
		await File.WriteAllTextAsync(manifest, "# First revision");
		using var window = new SkillCatalogWindow(paths, [], [], null, native);
		try
		{
			await AfterOperationAsync(window, window.Show);
			window.FindControl<Expander>("SourceEditor")!.IsExpanded = true;
			window.FindControl<TextBox>("SourceInput")!.Text = source;
			await ClickAsync(window, "AddSource");
			window.FindControl<Expander>("SourceEditor")!.IsExpanded.ShouldBeFalse();
			window.FindControl<TabItem>("AvailabilityTab")!.IsVisible.ShouldBeTrue();
			window.FindControl<Button>("Update")!.IsVisible.ShouldBeFalse();
			await ClickAsync(window, "Install");
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Apply installation");
			var installed = Path.Combine(native, "review", "SKILL.md");
			File.Exists(installed).ShouldBeFalse();
			await ClickAsync(window, "Apply");
			File.ReadAllText(installed).ShouldBe("# First revision");
			window.FindControl<TreeView>("Skills")!.Items.OfType<TreeViewItem>().Single().Items.Count.ShouldBe(1);
			await File.WriteAllTextAsync(manifest, "# Second revision");
			await ClickAsync(window, "Update");
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Apply update");
			window.FindControl<TextBox>("Contents")!.Text.ShouldBe("# Second revision");
			File.ReadAllText(installed).ShouldBe("# First revision");
			await ClickAsync(window, "Apply");
			File.ReadAllText(installed).ShouldBe("# Second revision");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task OpeningCatalogDiscoversConfiguredNativePackageAndShowsItsProfileAndOrigin()
	{
		using var temp = TemporaryDirectory.Create();
		var home = Path.Combine(temp.Path, "isolated-claude");
		var package = Directory.CreateDirectory(Path.Combine(home, "skills", "native-only")).FullName;
		var manifest = Path.Combine(package, "SKILL.md");
		await File.WriteAllTextAsync(manifest, "---\nname: native-only\n---\nbody");
		AgentProfileRecord profile = new("test-claude", AgentKind.Claude, "Work profile", "claude", null, "pwsh") { NativeConfigDirectory = home };
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [profile],
			[temp.Path], temp.Path, Path.Combine(temp.Path, "installed"), openCatalog: true);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var skills = window.FindControl<TreeView>("Skills")!;
			window.FindControl<TabControl>("Details")!.SelectedIndex.ShouldBe(0);
			skills.SelectedItem = skills.Items.OfType<TreeViewItem>().SelectMany(source => source.Items.OfType<TreeViewItem>())
				.Single(item => Equals(item.Header, "native-only"));
			var origin = window.FindControl<SelectableTextBlock>("Origin")!.Text.ShouldNotBeNull();
			origin.ShouldContain(manifest);
			origin.ShouldContain("Work profile");
			origin.ShouldContain("Claude");
		}
		finally
		{
			window.Close();
		}
	}

	private static Task ClickAsync(SkillCatalogWindow window, string name)
	{
		var button = window.FindControl<Button>(name)!;
		button.IsEnabled.ShouldBeTrue();
		button.IsVisible.ShouldBeTrue();
		return AfterOperationAsync(window, () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
	}

	private static async Task AfterOperationAsync(Window window, Action action)
	{
		var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
		{
			if (e.Property == InputElement.IsEnabledProperty && window.IsEnabled)
			{
				idle.TrySetResult();
			}
		}
		window.PropertyChanged += OnPropertyChanged;
		try
		{
			action();
			if (!window.IsEnabled)
			{
				await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
		}
		finally
		{
			window.PropertyChanged -= OnPropertyChanged;
		}
	}
}

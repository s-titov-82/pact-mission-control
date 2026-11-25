using System.Collections.Specialized;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class McpCatalogWindowTests
{
	[AvaloniaTest]
	public async Task ProjectMappingPreviewsAndAppliesChoicesForMultipleProfilesTogether()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var catalog = new McpCatalogService(paths);
		var snapshot = catalog.Read();
		await catalog.SaveAsync(snapshot, snapshot.Settings with { Servers = [new("docs", "https://example.test/mcp", null, [], null)] }, CancellationToken.None);
		var profile = new AgentProfileRecord("claude", AgentKind.Claude, "Work Claude", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "native") };
		var codex = new AgentProfileRecord("codex", AgentKind.Codex, "Work Codex", "codex", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "codex-native") };
		var window = new McpCatalogWindow(paths, [profile, codex], [temp.Path], temp.Path);
		try
		{
			window.Show();
			Assert.That(window.FindControl<TabControl>("Catalog")!.SelectedItem, Is.SameAs(window.FindControl<TabItem>("MappingTab")));
			var rows = window.FindControl<StackPanel>("MappingRows");
			Assert.That(rows, Is.Not.Null, "Project settings must offer a shared matrix for the two profiles.");
			var scopes = window.FindControl<TreeView>("Scopes")!;
			var project = scopes.SelectedItem;
			scopes.SelectedItem = scopes.Items[0];
			Choose("On", "Work Claude");
			var folder = scopes.GetLogicalDescendants().OfType<TreeViewItem>()
				.Single(item => AutomationProperties.GetName(item) == $"Folder · {temp.Path}");
			scopes.SelectedItem = folder;
			Assert.That(FindChoice("Inherit", "Work Claude").IsChecked, Is.True);
			Assert.That(ToolTip.GetTip(FindChoice("Inherit", "Work Claude")), Is.EqualTo("Inherit: On"));
			Choose("Off", "Work Claude");
			scopes.SelectedItem = project;
			Assert.That(ToolTip.GetTip(FindChoice("Inherit", "Work Claude")), Is.EqualTo("Inherit: Off"));
			Assert.That(FindChoice("Inherit", "Work Codex").IsChecked, Is.True);
			Choose("Off", "Work Codex");
			Assert.That(FindChoice("Off", "Work Codex").IsChecked, Is.True);
			Assert.That(FindChoice("Inherit", "Work Codex").IsChecked, Is.False);
			Click(window, "ReviewAvailability");
			var preview = window.FindControl<SelectableTextBlock>("PreviewText")!.Text;
			Assert.That(preview, Does.Contain("Work Claude").And.Contain("Work Codex").And.Contain(catalog.SettingsPath).And.Contain(temp.Path));
			Assert.That(catalog.Read().Settings.Selections, Is.Empty);
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Selections, Is.EquivalentTo(new[]
			{
				new McpCatalogSelection(profile.Id, profile.Kind, null, "docs", true),
				new McpCatalogSelection(profile.Id, profile.Kind, temp.Path, "docs", false) { IsFolder = true },
				new McpCatalogSelection(codex.Id, codex.Kind, temp.Path, "docs", false)
			}));

			ToggleButton FindChoice(string choice, string displayName)
			{
				return rows!.GetLogicalDescendants().OfType<ToggleButton>()
					.Single(button => AutomationProperties.GetName(button) == $"{choice}: docs — {displayName}");
			}
			void Choose(string choice, string displayName)
			{
				FindChoice(choice, displayName).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			}
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task UntrustedCodexProjectDisablesOnlyChoicesForProjectOnlyDeclarations()
	{
		using var temp = TemporaryDirectory.Create();
		var project = Path.Combine(temp.Path, "project");
		var projectNative = Directory.CreateDirectory(Path.Combine(project, ".codex")).FullName;
		var userNative = Directory.CreateDirectory(Path.Combine(temp.Path, "codex")).FullName;
		await File.WriteAllTextAsync(Path.Combine(projectNative, "config.toml"), """
			[mcp_servers.project_only]
			url = "https://example.test/mcp"
			[mcp_servers.global]
			url = "https://project.test/mcp"
			""");
		await File.WriteAllTextAsync(Path.Combine(userNative, "config.toml"), "[mcp_servers.global]\nurl = 'https://example.test/mcp'\n");
		var profile = new AgentProfileRecord("codex", AgentKind.Codex, "Work Codex", "codex", null, "pwsh") { NativeConfigDirectory = userNative };
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var window = new McpCatalogWindow(paths, [profile], [project], project);
		var servers = window.FindControl<TreeView>("Servers")!;
		var inspected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		servers.Items.CollectionChanged += Changed;
		try
		{
			window.Show();
			await inspected.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var matrix = window.FindControl<StackPanel>("MappingRows")!;
			var options = matrix.GetLogicalDescendants().OfType<ToggleButton>().ToDictionary(button => AutomationProperties.GetName(button)!);
			Assert.That(options["On: project_only (native) — Work Codex"].IsEnabled, Is.False);
			Assert.That(options["Off: project_only (native) — Work Codex"].IsEnabled, Is.False);
			Assert.That(options["Inherit: project_only (native) — Work Codex"].IsEnabled, Is.True);
			Assert.That(options["On: global (native) — Work Codex"].IsEnabled, Is.True);
			Assert.That(matrix.GetLogicalDescendants().OfType<TextBlock>().Select(label => label.Text), Does.Contain("Project not trusted in Codex"));
		}
		finally
		{
			servers.Items.CollectionChanged -= Changed;
			window.Close();
		}

		void Changed(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (servers.Items.Count == 2)
			{
				inspected.TrySetResult();
			}
		}
	}

	[AvaloniaTest]
	public async Task CatalogSharesDisablesAndRemovesNativeServersInTheSelectedFiles()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var catalog = new McpCatalogService(paths);
		var snapshot = catalog.Read();
		await catalog.SaveAsync(snapshot, snapshot.Settings with { Servers = [new("docs", "https://example.test/mcp", null, [], null)] }, CancellationToken.None);
		var nativeDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "claude")).FullName;
		var nativeFile = Path.Combine(nativeDirectory, ".claude.json");
		const string nativeContents = """{"mcpServers":{"docs":{"type":"http","url":"https://native.test/mcp","headers":{"X-Jira-Token":"jira-old","apiKey":"api-old","X-GitLab-Token":"gitlab-old"}}}}""";
		await File.WriteAllTextAsync(nativeFile, nativeContents);
		var profile = new AgentProfileRecord("claude", AgentKind.Claude, "Work Claude", "claude", null, "pwsh")
		{ NativeConfigDirectory = nativeDirectory };
		var codex = new AgentProfileRecord("codex", AgentKind.Codex, "Work Codex", "codex", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "codex-native") };
		var window = new McpCatalogWindow(paths, [codex, profile], []);
		var sources = window.FindControl<TreeView>("Servers")!;
		var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		sources.Items.CollectionChanged += OnSourcesChanged;
		try
		{
			window.Show();
			await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var groups = sources.Items.OfType<TreeViewItem>().ToDictionary(group => (string)ToolTip.GetTip(group)!);
			Assert.That(groups.Keys, Is.EquivalentTo(new[] { catalog.SettingsPath, nativeFile }));
			var edit = window.FindControl<StackPanel>("DefinitionActions")!;
			sources.SelectedItem = groups[catalog.SettingsPath].Items[0];
			Assert.That(edit.IsVisible, Is.True);
			sources.SelectedItem = groups[nativeFile].Items[0];
			Assert.That(edit.IsVisible, Is.False);
			Assert.That(window.FindControl<SelectableTextBlock>("ServerDetails")!.Text, Does.Contain(nativeFile));
			Assert.That(window.FindControl<StackPanel>("NativeDefinitionEditor")!.IsVisible, Is.True);
			var editor = window.FindControl<TextBox>("NativeDefinitionContent")!;
			var declaration = JsonNode.Parse(editor.Text!)!;
			Assert.That(declaration["url"]!.GetValue<string>(), Is.EqualTo("https://native.test/mcp"));
			Assert.That(declaration["headers"]!["apiKey"]!.GetValue<string>(), Is.EqualTo("api-old"));
			declaration["url"] = "https://updated.test/mcp";
			declaration["headers"]!["apiKey"] = "fixture-replacement";
			declaration["headers"]!["X-GitLab-Token"] = "gitlab-new";
			editor.Text = declaration.ToJsonString();
			var draftText = editor.Text;
			Assert.That(sources.IsEnabled, Is.False);
			Assert.That(window.FindControl<TabItem>("MappingTab")!.IsEnabled, Is.False);
			Click(window, "ReviewNativeDefinition");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text,
				Does.Contain(nativeFile).And.Not.Contain("fixture-replacement").And.Not.Contain("gitlab-new"));
			Assert.That(File.ReadAllText(nativeFile), Is.EqualTo(nativeContents));
			var external = JsonNode.Parse(nativeContents)!;
			external["Проект"] = "имя + &";
			await File.WriteAllTextAsync(nativeFile, external.ToJsonString());
			await ApplyAsync(window, "A native configuration changed");
			Assert.That(editor.Text, Is.EqualTo(draftText));
			Click(window, "ReviewNativeDefinition");
			await ApplyAsync(window, "Saved.");
			var updatedContents = File.ReadAllText(nativeFile);
			Assert.That(updatedContents, Does.Contain("Проект").And.Contain("имя + &"));
			var updated = JsonNode.Parse(updatedContents)!["mcpServers"]!["docs"]!;
			Assert.That(updated["url"]!.GetValue<string>(), Is.EqualTo("https://updated.test/mcp"));
			Assert.That(updated["headers"]!["apiKey"]!.GetValue<string>(), Is.EqualTo("fixture-replacement"));
			Assert.That(updated["headers"]!["X-GitLab-Token"]!.GetValue<string>(), Is.EqualTo("gitlab-new"));
			Assert.That(updated["headers"]!["X-Jira-Token"]!.GetValue<string>(), Is.EqualTo("jira-old"));
			Assert.That(JsonNode.Parse(editor.Text!)!["headers"]!["apiKey"]!.GetValue<string>(), Is.EqualTo("fixture-replacement"));

			Click(window, "ShareDefinition");
			window.FindControl<ListBox>("NativeShareTargets")!.SelectedIndex = 0;
			Click(window, "ReviewNativeShare");
			var destination = Path.Combine(codex.NativeConfigDirectory!, "config.toml");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text,
				Does.Contain(destination).And.Not.Contain("fixture-replacement"));
			Assert.That(File.Exists(destination), Is.False);
			await ApplyAsync(window, "Saved.");
			Assert.That(McpCatalogService.ReadNative(codex, null).Single().Name, Is.EqualTo("docs"));
			Assert.That(catalog.Read().Settings.Servers, Has.Length.EqualTo(1));
			Assert.That(File.ReadAllText(nativeFile), Is.EqualTo(updatedContents));

			SelectOrigin();
			var nativeState = window.FindControl<TextBlock>("NativeState")!;
			var enable = window.FindControl<Button>("EnableNative")!;
			var disable = window.FindControl<Button>("DisableNative")!;
			Assert.That(nativeState.Text, Is.EqualTo("Native configuration: Enabled"));
			Assert.That(enable.IsVisible, Is.False);
			Assert.That(disable.IsVisible, Is.True);
			Click(window, "DisableNative");
			var settings = Path.Combine(nativeDirectory, "settings.json");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text, Does.Contain(settings));
			Assert.That(File.Exists(settings), Is.False);
			Assert.That(nativeState.Text, Is.EqualTo("Native configuration: Enabled"));
			await ApplyAsync(window, "Saved.");
			Assert.That(JsonNode.Parse(File.ReadAllText(settings))!["deniedMcpServers"]![0]!["serverName"]!.GetValue<string>(), Is.EqualTo("docs"));
			Assert.That((sources.SelectedItem as TreeViewItem)?.Header, Does.Contain("docs").And.Contain("Disabled"));
			Assert.That(nativeState.Text, Is.EqualTo("Native configuration: Disabled"));
			Assert.That(enable.IsVisible, Is.True);
			Assert.That(disable.IsVisible, Is.False);
			var tabs = window.FindControl<TabControl>("Catalog")!;
			var catalogTab = tabs.SelectedItem;
			tabs.SelectedItem = window.FindControl<TabItem>("MappingTab");
			var mappingRows = window.FindControl<StackPanel>("MappingRows")!;
			var on = mappingRows.GetLogicalDescendants().OfType<ToggleButton>()
				.Single(button => AutomationProperties.GetName(button) == "On: docs (native) — Work Claude");
			Assert.That(on.IsEnabled, Is.False);
			tabs.SelectedItem = catalogTab;
			Click(window, "EnableNative");
			await ApplyAsync(window, "Saved.");
			Assert.That(nativeState.Text, Is.EqualTo("Native configuration: Enabled"));
			var mappingTab = window.FindControl<TabItem>("MappingTab");
			tabs.SelectedItem = mappingTab;
			var off = mappingRows.GetLogicalDescendants().OfType<ToggleButton>()
				.Single(button => AutomationProperties.GetName(button) == "Off: docs (native) — Work Claude");
			off.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Click(window, "ReviewAvailability");
			await ApplyAsync(window, "Saved.");
			tabs.SelectedItem = catalogTab;

			SelectOrigin();
			Click(window, "RemoveNative");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text, Does.Contain(nativeFile).And.Contain("stay visible"));
			await ApplyAsync(window, "Saved.");
			Assert.That(McpCatalogService.ReadNative(profile, null), Is.Empty);
			Assert.That(McpCatalogService.ReadNative(codex, null).Single().Name, Is.EqualTo("docs"));
			tabs.SelectedItem = mappingTab;
			var inherit = mappingRows.GetLogicalDescendants().OfType<ToggleButton>()
				.Single(button => AutomationProperties.GetName(button) == "Inherit: docs (native) — Work Claude");
			inherit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Click(window, "ReviewAvailability");
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Selections, Is.Empty);
		}
		finally
		{
			sources.Items.CollectionChanged -= OnSourcesChanged;
			window.Close();
		}

		void OnSourcesChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (sources.Items.Count == 2)
			{
				loaded.TrySetResult();
			}
		}

		void SelectOrigin()
		{
			sources.SelectedItem = sources.Items.OfType<TreeViewItem>().Single(group => Equals(ToolTip.GetTip(group), nativeFile)).Items[0];
		}
	}

	[AvaloniaTest]
	public async Task DefinitionDraftSurvivesConflictAndCanBeAppliedAfterReviewingAgain()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var catalog = new McpCatalogService(paths);
		var window = new McpCatalogWindow(paths, [], [temp.Path]);
		try
		{
			window.Show();
			Click(window, "AddServer");
			Assert.That(window.FindControl<TabItem>("MappingTab")!.IsEnabled, Is.False);
			Assert.That(window.FindControl<TreeView>("Servers")!.IsEnabled, Is.False);
			Assert.That(window.FindControl<Button>("AddServer")!.IsVisible, Is.False);
			window.FindControl<TextBox>("ServerId")!.Text = "docs";
			var url = window.FindControl<TextBox>("ServerUrl")!;
			url.Text = "https://example.test/mcp";
			Click(window, "ReviewDefinition");
			var external = catalog.Read();
			await catalog.SaveAsync(external, external.Settings with { Servers = [new("other", "https://other.test/mcp", null, [], null)] }, CancellationToken.None);
			await ApplyAsync(window, "The MCP catalog changed");
			Assert.That(window.FindControl<Border>("Preview")!.IsVisible, Is.False);
			Assert.That(url.Text, Is.EqualTo("https://example.test/mcp"));
			Click(window, "ReviewDefinition");
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Servers.Select(server => server.Id), Is.EquivalentTo(new[] { "docs", "other" }));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task EditAndRemoveDefinitionPreviewTheProfileAndClearItsSelections()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var catalog = new McpCatalogService(paths);
		var snapshot = catalog.Read();
		var profile = new AgentProfileRecord("claude", AgentKind.Claude, "Work Claude", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "native") };
		await catalog.SaveAsync(snapshot, snapshot.Settings with
		{
			Servers = [new("docs", "https://example.test/mcp", null, [], null)],
			Selections = [new(profile.Id, profile.Kind, null, "docs", true)]
		}, CancellationToken.None);
		var window = new McpCatalogWindow(paths, [profile], []);
		try
		{
			window.Show();
			Assert.That(window.FindControl<StackPanel>("DefinitionEditor")!.IsVisible, Is.True);
			window.FindControl<TextBox>("ServerUrl")!.Text = "https://updated.test/mcp";
			Click(window, "ReviewDefinition");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text, Does.Contain(profile.DisplayName));
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Servers.Single().Url, Is.EqualTo("https://updated.test/mcp"));
			Click(window, "RemoveDefinition");
			Assert.That(window.FindControl<SelectableTextBlock>("PreviewText")!.Text, Does.Contain(profile.DisplayName));
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Servers, Is.Empty);
			Assert.That(catalog.Read().Settings.Selections, Is.Empty);
		}
		finally
		{
			window.Close();
		}
	}

	private static async Task ApplyAsync(Window window, string expectedStatus)
	{
		var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var status = window.FindControl<TextBlock>("Status")!;
		status.PropertyChanged += Changed;
		try
		{
			Click(window, "Apply");
			await applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			status.PropertyChanged -= Changed;
		}

		void Changed(object? sender, AvaloniaPropertyChangedEventArgs args)
		{
			if (args.Property == TextBlock.TextProperty && status.Text?.StartsWith(expectedStatus, StringComparison.Ordinal) == true)
			{
				applied.TrySetResult();
			}
		}
	}

	private static void Click(Window window, string name) => window.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}

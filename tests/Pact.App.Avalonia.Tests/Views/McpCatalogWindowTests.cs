using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class McpCatalogWindowTests
{
	[AvaloniaTest]
	public async Task ProjectAvailabilityPreviewIdentifiesFileAndProfileAndWritesOnlyOnApply()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "data"));
		var catalog = new McpCatalogService(paths);
		var snapshot = catalog.Read();
		await catalog.SaveAsync(snapshot, snapshot.Settings with { Servers = [new("docs", "https://example.test/mcp", null, [], null)] }, CancellationToken.None);
		var profile = new AgentProfileRecord("claude", AgentKind.Claude, "Work Claude", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "native") };
		var window = new McpCatalogWindow(paths, [profile], [temp.Path], temp.Path);
		try
		{
			window.Show();
			var selection = window.FindControl<ComboBox>("Selection")!;
			Assert.That(selection.SelectedItem, Is.EqualTo("Inherit"));
			selection.SelectedIndex = 1;
			Click(window, "ReviewAvailability");
			var preview = window.FindControl<SelectableTextBlock>("PreviewText")!.Text;
			Assert.That(preview, Does.Contain("Work Claude").And.Contain(catalog.SettingsPath).And.Contain(temp.Path));
			Assert.That(catalog.Read().Settings.Selections, Is.Empty);
			await ApplyAsync(window, "Saved.");
			Assert.That(catalog.Read().Settings.Selections, Is.EqualTo(new[] { new McpCatalogSelection(profile.Id, profile.Kind, temp.Path, "docs", true) }));
		}
		finally
		{
			window.Close();
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
			Assert.That(window.FindControl<ComboBox>("Profiles")!.IsEnabled, Is.False);
			Assert.That(window.FindControl<ComboBox>("Scopes")!.IsEnabled, Is.False);
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
			Click(window, "EditDefinition");
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

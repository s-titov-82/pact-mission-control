using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed partial class SkillCatalogWindowTests
{
	[AvaloniaTest]
	public async Task A_saved_source_with_only_bound_packages_leaves_the_mapping_controls_in_place()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var source = Path.Combine(temp.Path, "source");
		var package = Directory.CreateDirectory(Path.Combine(source, "review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Review");
		var service = new SkillCatalogService(paths.SettingsDirectory, installed);
		var skills = await service.BrowseAsync(source);
		await Task.Run(() => service.Apply(service.Preview(skills.Single())));
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "skill-sources.json"),
			"{\"sources\":[\"https://github.com/example/team\"]}");
		var profile = new AgentProfileRecord("work", AgentKind.Claude, "Work", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "claude") };
		var release = new TaskCompletionSource<IReadOnlyList<CatalogSkill>>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var window = new SkillCatalogWindow(paths, [profile], [], null, installed, profile.Id,
			browseSavedSource: (_, token) => release.Task.WaitAsync(token));
		try
		{
			await AfterOperationAsync(window, window.Show);
			await window.MatrixReady.WaitAsync(TimeSpan.FromSeconds(10));
			var host = window.FindControl<ContentControl>("MatrixHost")!;
			var expand = host.GetLogicalDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), source));
			await FinishSavedSourcesAsync(window, () => release.SetResult(skills));
			await window.MatrixReady.WaitAsync(TimeSpan.FromSeconds(10));
			host.GetLogicalDescendants().OfType<Button>()
				.Single(button => Equals(ToolTip.GetTip(button), source)).ShouldBeSameAs(expand);
		}
		finally { window.Close(); }
	}

	[AvaloniaTest]
	public async Task The_first_saved_source_result_preserves_an_existing_operation_error()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var source = Path.Combine(temp.Path, "source");
		var package = Directory.CreateDirectory(Path.Combine(source, "review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Review");
		var service = new SkillCatalogService(paths.SettingsDirectory, installed);
		var skills = await service.BrowseAsync(source);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "skill-sources.json"),
			"{\"sources\":[\"https://github.com/example/team\"]}");
		var release = new TaskCompletionSource<IReadOnlyList<CatalogSkill>>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var window = new SkillCatalogWindow(paths, [], [], null, installed,
			browseSavedSource: (_, token) => release.Task.WaitAsync(token));
		try
		{
			await AfterOperationAsync(window, window.Show);
			window.FindControl<Expander>("SourceEditor")!.IsExpanded = true;
			window.FindControl<TextBox>("SourceInput")!.Text = Directory.CreateDirectory(Path.Combine(temp.Path, "empty")).FullName;
			await ClickAsync(window, "AddSource");
			var status = window.FindControl<SelectableTextBlock>("Status")!;
			var error = status.Text.ShouldNotBeNull();
			error.ShouldContain("No SKILL.md found");
			await FinishSavedSourcesAsync(window, () => release.SetResult(skills));
			window.FindControl<TextBlock>("SkillTitle")!.Text.ShouldBe("review");
			status.Text.ShouldBe(error);
		}
		finally { window.Close(); }
	}

	private static async Task FinishSavedSourcesAsync(SkillCatalogWindow window, Action release)
	{
		const string reading = "Reading saved GitHub sources";
		var status = window.FindControl<SelectableTextBlock>("LoadStatus")!;
		status.Text.ShouldNotBeNull().ShouldContain(reading);
		var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		status.PropertyChanged += (_, e) =>
		{
			if (e.Property == SelectableTextBlock.TextProperty && status.Text?.Contains(reading, StringComparison.Ordinal) != true)
			{
				finished.TrySetResult();
			}
		};
		release();
		await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
		Dispatcher.UIThread.RunJobs();
	}

	[AvaloniaTest]
	public async Task Successful_installed_refresh_replaces_obsolete_discovery_warnings()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var staging = Directory.CreateDirectory(Path.Combine(installed, ".pact-skill-recovered")).FullName;
		await File.WriteAllTextAsync(Path.Combine(staging, "SKILL.md"), "# Staged");
		var source = Path.Combine(temp.Path, "team");
		var package = Directory.CreateDirectory(Path.Combine(source, "review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Review");
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(Path.Combine(paths.SettingsDirectory, "skill-sources.json"),
			System.Text.Json.JsonSerializer.Serialize(new { sources = new[] { source } }));
		using var window = new SkillCatalogWindow(paths, [], [], null, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var status = window.FindControl<SelectableTextBlock>("LoadStatus")!;
			status.Text.ShouldNotBeNull().ShouldContain(staging);
			Directory.Move(staging, Path.Combine(temp.Path, "recovered"));
			await ClickAsync(window, "Install");
			await ClickAsync(window, "Apply");
			status.Text.ShouldBeEmpty();
			window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldBe("Applied.");
		}
		finally { window.Close(); }
	}

	[AvaloniaTest]
	public async Task Finding_duplicates_refreshes_missing_installed_rows_when_a_load_error_is_resolved()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var package = Directory.CreateDirectory(Path.Combine(installed, "restored")).FullName;
		await File.WriteAllTextAsync(Path.Combine(package, "SKILL.md"), "# Restored");
		Directory.CreateDirectory(paths.SettingsDirectory);
		var registry = Path.Combine(paths.SettingsDirectory, "skill-installations.json");
		await File.WriteAllTextAsync(registry, "{");
		using var window = new SkillCatalogWindow(paths, [], [], null, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var status = window.FindControl<SelectableTextBlock>("LoadStatus")!;
			var skills = window.FindControl<TreeView>("Skills")!;
			status.Text.ShouldNotBeNull().ShouldContain("Could not load installed skills");
			skills.Items.ShouldBeEmpty();

			await File.WriteAllTextAsync(registry, "{}");
			await ClickAsync(window, "FindDuplicates");

			status.Text.ShouldBeEmpty();
			skills.Items.ShouldNotBeEmpty();
			window.FindControl<Grid>("DuplicatePanel")!.IsVisible.ShouldBeTrue();
		}
		finally { window.Close(); }
	}

	[AvaloniaTest]
	public async Task DuplicateReportShowsDifferingScriptsAndPreservesBothCopies()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var first = Directory.CreateDirectory(Path.Combine(installed, "sample")).FullName;
		var second = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills", "sample")).FullName;
		foreach (var directory in new[] { first, second })
		{
			File.WriteAllText(Path.Combine(directory, "SKILL.md"), "# same");
			File.WriteAllText(Path.Combine(directory, "run.ps1"), first);
			var identical = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(directory)!, "zzz-identical")).FullName;
			File.WriteAllText(Path.Combine(identical, "SKILL.md"), "# identical package");
		}
		using var window = new SkillCatalogWindow(paths, [], [], null, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			File.WriteAllText(Path.Combine(second, "run.ps1"), second);
			await ClickAsync(window, "FindDuplicates");
			var report = string.Join(Environment.NewLine, window.FindControl<StackPanel>("DuplicateLocations")!
				.GetLogicalDescendants().OfType<SelectableTextBlock>().Select(text => text.Text));
			report.ShouldContain("Same name, different contents");
			report.ShouldContain("run.ps1");
			report.ShouldContain(first);
			report.ShouldContain(second);
			var identicalIndex = report.IndexOf("zzz-identical · Identical independent copies", StringComparison.Ordinal);
			identicalIndex.ShouldBeGreaterThanOrEqualTo(0);
			identicalIndex.ShouldBeLessThan(report.IndexOf("sample · Same name, different contents", StringComparison.Ordinal));
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeFalse();
			foreach (var directory in new[] { first, second })
			{
				File.ReadAllText(Path.Combine(directory, "run.ps1")).ShouldBe(directory);
			}
		}
		finally { window.Close(); }
	}

	[AvaloniaTest]
	public async Task LinkRepositoryPreviewsConflictThenBindsExactCandidateToRenamedLocalFolderOnlyOnApply()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var installed = Path.Combine(temp.Path, "shared");
		var local = Directory.CreateDirectory(Path.Combine(installed, "local-copy")).FullName;
		var repo = Directory.CreateDirectory(Path.Combine(temp.Path, "team")).FullName;
		const string manifest = "---\nname: sample\n---\nbody";
		File.WriteAllText(Path.Combine(local, "SKILL.md"), manifest);
		File.WriteAllText(Path.Combine(local, "script.ps1"), "installed");
		foreach (var name in new[] { "first", "second" })
		{
			var package = Directory.CreateDirectory(Path.Combine(repo, name)).FullName;
			File.WriteAllText(Path.Combine(package, "SKILL.md"), manifest);
			File.WriteAllText(Path.Combine(package, "script.ps1"), name == "first" ? "different" : "installed");
		}
		var service = new SkillCatalogService(paths.SettingsDirectory, installed);
		using var window = new SkillCatalogWindow(paths, [], [], null, installed);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await ClickAsync(window, "LinkSource");
			window.FindControl<TextBox>("BindingSource")!.Text = repo;
			await ClickAsync(window, "ReadBinding");
			var candidates = window.FindControl<ComboBox>("BindingCandidates")!;
			candidates.ItemCount.ShouldBe(2);
			await AfterOperationAsync(window, () => candidates.SelectedIndex = 0);
			window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull().ShouldContain("differ");
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeFalse();
			await AfterOperationAsync(window, () => candidates.SelectedIndex = 1);
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Bind update source");
			service.GetBindings().ShouldBeEmpty();
			await ClickAsync(window, "Apply");
			service.GetBindings()["local-copy"].RelativePath.ShouldBe("second");
			File.ReadAllText(Path.Combine(local, "script.ps1")).ShouldBe("installed");
			Directory.Exists(Path.Combine(installed, "second")).ShouldBeFalse();
			var update = await service.CheckUpdateAsync("local-copy");
			update.Destination.ShouldBe(local);
			update.Changes.ShouldBeEmpty();
		}
		finally { window.Close(); }
	}
}

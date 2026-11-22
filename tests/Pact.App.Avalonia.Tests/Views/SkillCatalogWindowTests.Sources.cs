using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views.Settings;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed partial class SkillCatalogWindowTests
{
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
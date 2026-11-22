using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views.Settings;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Tests.Views;

public sealed partial class SkillCatalogWindowTests
{
	[AvaloniaTest]
	public async Task Rename_folder_reviews_the_selected_installation_and_moves_it_only_after_confirmation()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var native = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills")).FullName;
		var original = Directory.CreateDirectory(Path.Combine(native, "z-before")).FullName;
		var occupied = Directory.CreateDirectory(Path.Combine(native, "a-keep")).FullName;
		var renamed = Path.Combine(native, "z-after");
		const string manifest = "---\nname: z-before\n---\n# Original";
		await File.WriteAllTextAsync(Path.Combine(original, "SKILL.md"), manifest);
		await File.WriteAllTextAsync(Path.Combine(occupied, "SKILL.md"), "# Other");
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var tree = window.FindControl<TreeView>("Skills")!;
			tree.SelectedItem = tree.Items.OfType<TreeViewItem>().SelectMany(node => node.Items.OfType<TreeViewItem>())
				.Single(item => Equals(item.Header, "z-before"));
			await ClickAsync(window, "RenameFolder");
			var input = window.FindControl<TextBox>("RenameInput")!;
			input.Text.ShouldBe("z-before");
			input.Text = "a-keep";
			await ClickAsync(window, "ReviewRename");
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeFalse();
			input.Text = "z-after";
			await ClickAsync(window, "ReviewRename");
			var status = window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull();
			status.ShouldContain(original);
			status.ShouldContain(renamed);
			Directory.Exists(renamed).ShouldBeFalse();
			input.Text = "different";
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeFalse();
			await ClickAsync(window, "CancelRename");
			window.FindControl<Grid>("RenameEditor")!.IsVisible.ShouldBeFalse();
			Directory.Exists(original).ShouldBeTrue();
			await ClickAsync(window, "RenameFolder");
			input.Text = "z-after";
			await ClickAsync(window, "ReviewRename");
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Rename folder");
			await ClickAsync(window, "Apply");
			Directory.Exists(original).ShouldBeFalse();
			(await File.ReadAllTextAsync(Path.Combine(renamed, "SKILL.md"))).ShouldBe(manifest);
			window.FindControl<TextBlock>("SkillTitle")!.Text.ShouldBe("z-after (z-before)");
			(await File.ReadAllTextAsync(Path.Combine(occupied, "SKILL.md"))).ShouldBe("# Other");
			window.FindControl<SelectableTextBlock>("Origin")!.Text.ShouldNotBeNull().ShouldContain(renamed);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task System_package_shows_no_destructive_actions_in_its_card_or_duplicate_report()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var system = Directory.CreateDirectory(Path.Combine(temp.Path, ".codex", "skills", ".system", "managed")).FullName;
		var other = Directory.CreateDirectory(Path.Combine(shared, "independent")).FullName;
		foreach (var location in new[] { system, other })
		{
			await File.WriteAllTextAsync(Path.Combine(location, "SKILL.md"), "# Equal");
		}
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			var tree = window.FindControl<TreeView>("Skills")!;
			tree.SelectedItem = tree.Items.OfType<TreeViewItem>().SelectMany(node => node.Items.OfType<TreeViewItem>())
				.Single(item => Equals(item.Header, "managed"));
			window.FindControl<Button>("DeletePackage")!.IsVisible.ShouldBeFalse();
			window.FindControl<Button>("MakeShared")!.IsVisible.ShouldBeFalse();
			window.FindControl<Button>("RenameFolder")!.IsVisible.ShouldBeFalse();
			await ClickAsync(window, "FindDuplicates");
			var names = window.FindControl<StackPanel>("DuplicateLocations")!.GetLogicalDescendants()
				.OfType<Button>().Select(AutomationProperties.GetName).ToArray();
			names.ShouldNotContain($"Delete: {system}");
			names.ShouldNotContain($"Make shared: {system}");
			names.ShouldContain($"Delete: {other}");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Duplicate_location_delete_requires_confirmation_and_refreshes_the_report()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var first = Directory.CreateDirectory(Path.Combine(shared, "pair")).FullName;
		var second = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills", "pair")).FullName;
		foreach (var location in new[] { first, second })
		{
			await File.WriteAllTextAsync(Path.Combine(location, "SKILL.md"), "# Same package");
		}
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await ClickAsync(window, "FindDuplicates");
			var rows = window.FindControl<StackPanel>("DuplicateLocations")!;
			rows.GetLogicalDescendants().OfType<SelectableTextBlock>().ShouldContain(label => label.Text == $"Shared · {first}");
			var delete = rows.GetLogicalDescendants().OfType<Button>()
				.Single(b => AutomationProperties.GetName(b) == $"Delete: {second}");
			await AfterOperationAsync(window, () => delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull().ShouldContain(second);
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Delete directory");
			await ClickAsync(window, "CancelPreview");
			File.Exists(Path.Combine(second, "SKILL.md")).ShouldBeTrue();
			await AfterOperationAsync(window, () => delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			await ClickAsync(window, "Apply");
			Directory.Exists(second).ShouldBeFalse();
			File.Exists(Path.Combine(first, "SKILL.md")).ShouldBeTrue();
			window.FindControl<SelectableTextBlock>("DuplicateReport")!.Text.ShouldBe("No independent duplicate packages found.");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task DuplicateCleanupOffersDeletionOnlyForTheIndependentCopyOfASharedPackage()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var native = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills", "review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(native, "SKILL.md"), "# Review");
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await ClickAsync(window, "MakeShared");
			await ClickAsync(window, "Apply");
			var copy = Directory.CreateDirectory(Path.Combine(temp.Path, ".codex", "skills", "review")).FullName;
			File.Copy(Path.Combine(native, "SKILL.md"), Path.Combine(copy, "SKILL.md"));
			await ClickAsync(window, "FindDuplicates");
			var report = window.FindControl<StackPanel>("DuplicateLocations")!;
			var deletes = report.GetLogicalDescendants().OfType<Button>().Where(button => Equals(button.Content, "Delete…")).ToArray();
			AutomationProperties.GetName(deletes.Single()).ShouldBe($"Delete: {copy}");
			await AfterOperationAsync(window, () => deletes.Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
			await ClickAsync(window, "Apply");
			report.Children.ShouldBeEmpty();
			File.Exists(Path.Combine(native, "SKILL.md")).ShouldBeTrue();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Make_shared_then_delete_only_the_selected_link_keeps_the_shared_package()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var native = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills", "review")).FullName;
		await File.WriteAllTextAsync(Path.Combine(native, "SKILL.md"), "# Review");
		using var window = new SkillCatalogWindow(new AppPaths(Path.Combine(temp.Path, "pact")), [], [], null, shared);
		try
		{
			await AfterOperationAsync(window, window.Show);
			await ClickAsync(window, "MakeShared");
			var destination = Path.Combine(shared, "review");
			window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull().ShouldContain(destination);
			Directory.Exists(destination).ShouldBeFalse();
			await ClickAsync(window, "Apply");
			File.Exists(Path.Combine(destination, "SKILL.md")).ShouldBeTrue();

			var locations = window.FindControl<ComboBox>("MaintenanceLocations")!;
			locations.IsVisible.ShouldBeTrue();
			locations.SelectedItem.ShouldBeNull();
			window.FindControl<Button>("DeletePackage")!.IsEnabled.ShouldBeFalse();
			await ClickAsync(window, "RenameFolder");
			locations.SelectedItem.ShouldBe(destination);
			window.FindControl<TextBox>("RenameInput")!.Text.ShouldBe("review");
			await ClickAsync(window, "CancelRename");
			locations.SelectedItem = native;
			window.FindControl<Button>("RenameFolder")!.IsVisible.ShouldBeFalse();
			await ClickAsync(window, "DeletePackage");
			var message = window.FindControl<SelectableTextBlock>("Status")!.Text.ShouldNotBeNull();
			message.ShouldContain(native);
			message.ShouldContain($"Link target: {destination}");
			window.FindControl<Button>("Apply")!.Content.ShouldBe("Delete link");
			locations.SelectedItem = destination;
			window.FindControl<Button>("Apply")!.IsVisible.ShouldBeFalse();
			locations.SelectedItem = native;
			await ClickAsync(window, "DeletePackage");
			await ClickAsync(window, "Apply");
			Directory.Exists(native).ShouldBeFalse();
			(await File.ReadAllTextAsync(Path.Combine(destination, "SKILL.md"))).ShouldBe("# Review");
		}
		finally
		{
			window.Close();
		}
	}
}
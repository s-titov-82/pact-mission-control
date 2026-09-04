using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class GitHelpersSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private string _dir => _directory.Path;
	private SettingsFileStore Store => new(_dir);
	private string FilePath => new AppPaths(_dir).GitHelpersPath;
	public void Dispose()
	{
		_directory.Dispose();
		GC.SuppressFinalize(this);
	}

	[Test]
	public async Task Load_maps_default_helper_and_save_round_trips_object_root_and_unknown_fields()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);

		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
		root["note"] = "keep";
		root["helpers"]!.AsArray()[0]!["customFlag"] = true;
		await File.WriteAllTextAsync(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);

		section.CommandItems.ShouldAllBe(item => item is GitCommandItemViewModel); // command items live in their own collection
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		first.Id.ShouldBe("tortoisegit");
		first.Name.ShouldBe("TortoiseGit");
		first.Executable.ShouldBe("TortoiseGitProc.exe");
		first.RegistryKey.ShouldBe(@"SOFTWARE\TortoiseGit");
		first.RegistryValue.ShouldBe("ProcPath");
		first.Actions.Count.ShouldBe(2);
		first.Actions[0].Slot.ShouldBe("history");
		first.Actions[0].Label.ShouldBe("History");
		first.Actions[0].ArgumentsText.ShouldBe("/command:log\n/path:{root}");

		first.Name = "Renamed";
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
		((string?)saved["note"]).ShouldBe("keep");
		var savedHelpers = saved["helpers"]!.AsArray();
		((string?)savedHelpers[0]!["name"]).ShouldBe("Renamed");
		((bool?)savedHelpers[0]!["customFlag"]).ShouldBe(true);
	}

	[Test]
	public async Task ArgumentsText_splits_on_save_and_drops_blank_lines()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		first.Actions[0].ArgumentsText = "/command:log\n\n  /path:{root}  \n\n";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
		var arguments = saved["helpers"]!.AsArray()[0]!["actions"]!.AsArray()[0]!["arguments"]!.AsArray();
		arguments.Count.ShouldBe(2);
		((string?)arguments[0]).ShouldBe("/command:log");
		((string?)arguments[1]).ShouldBe("/path:{root}");
	}

	[Test]
	public async Task Half_filled_registry_probe_blocks_save()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		first.RegistryValue = string.Empty; // key still filled -> exactly one field filled

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task Empty_executable_with_full_registry_probe_saves_but_without_probe_fails()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		first.Executable = string.Empty; // registry probe is still fully filled

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		first.RegistryKey = string.Empty;
		first.RegistryValue = string.Empty; // now neither executable nor probe is present

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task Emptying_both_registry_fields_removes_probe_property_on_save()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		first.RegistryKey = string.Empty;
		first.RegistryValue = string.Empty; // Executable stays filled, so this is still valid.

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
		var savedHelper = saved["helpers"]!.AsArray()[0]!.AsObject();
		savedHelper.ContainsKey("windowsRegistryProbe").ShouldBeFalse();
	}

	[Test]
	public async Task AddAction_and_RemoveAction_mark_section_dirty()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();
		section.IsDirty.ShouldBeFalse();

		var before = first.Actions.Count;
		first.AddAction();
		first.Actions.Count.ShouldBe(before + 1);
		first.SelectedAction.ShouldBeSameAs(first.Actions[^1]);
		section.IsDirty.ShouldBeTrue();

		first.RemoveAction(first.Actions[^1]);
		first.Actions.Count.ShouldBe(before);
		section.IsDirty.ShouldBeTrue();
	}

	[Test]
	public async Task Duplicate_helper_ids_block_save_with_status()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.HelperItems.OfType<GitHelperItemViewModel>().First();

		section.AddNewItem();
		var second = (GitHelperItemViewModel)section.HelperItems[^1];
		second.Id = first.Id;
		second.Name = "Duplicate";
		second.Executable = "dup.exe";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task Load_selects_first_command_and_first_helper_and_defaults_to_the_buttons_tab()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);

		section.ActiveTabIndex.ShouldBe(0);
		section.SelectedCommandItem.ShouldBeSameAs(section.CommandItems[0]);
		section.SelectedHelperItem.ShouldBeSameAs(section.HelperItems[0]);
	}

	[Test]
	public async Task SelectItem_finds_a_command_id_and_activates_the_buttons_tab()
	{
		var section = await LoadSectionAsync();
		section.ActiveTabIndex = 1;

		section.SelectItem("push", null);

		section.ActiveTabIndex.ShouldBe(0);
		var selected = section.SelectedCommandItem.ShouldBeOfType<GitCommandItemViewModel>();
		selected.Id.ShouldBe("push");
	}

	[Test]
	public async Task SelectItem_finds_a_helper_id_and_activates_the_external_helpers_tab()
	{
		var section = await LoadSectionAsync();
		var helperId = section.HelperItems.OfType<GitHelperItemViewModel>().First().Id;

		section.SelectItem(helperId, null);

		section.ActiveTabIndex.ShouldBe(1);
		var selected = section.SelectedHelperItem.ShouldBeOfType<GitHelperItemViewModel>();
		selected.Id.ShouldBe(helperId);
	}

	[Test]
	public async Task SelectItem_with_unknown_id_is_a_no_op()
	{
		var section = await LoadSectionAsync();
		var command = section.SelectedCommandItem;
		var helper = section.SelectedHelperItem;

		section.SelectItem("does-not-exist", null);

		section.SelectedCommandItem.ShouldBeSameAs(command);
		section.SelectedHelperItem.ShouldBeSameAs(helper);
	}

	private async Task<GitHelpersSectionViewModel> LoadSectionAsync()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		return section;
	}
}

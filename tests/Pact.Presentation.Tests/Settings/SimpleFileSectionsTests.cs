using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Prompting;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class SimpleFileSectionsTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private string _dir => _directory.Path;
	private SettingsFileStore Store => new(_dir);
	private AppPaths Paths => new(_dir);
	public void Dispose()
	{
		_directory.Dispose();
		GC.SuppressFinalize(this);
	}

	private LaunchProfilesSectionViewModel CreateProfilesSection() => new(Store); // ctor shape: (SettingsFileStore)

	[Test]
	public async Task Load_maps_default_profiles_and_save_round_trips_unknown_fields()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		// inject an unknown field
		var path = Paths.ShellProfilesPath;
		var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		node[0]!["customFlag"] = true;
		await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = CreateProfilesSection();
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<ShellProfileItemViewModel>();
		first.DisplayName = "Renamed";
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		((string?)saved[0]!["displayName"]).ShouldBe("Renamed");
		((bool?)saved[0]!["customFlag"]).ShouldBe(true); // unknown field preserved
	}

	[Test]
	public async Task Successful_save_clears_item_dirty_flag_but_failed_save_does_not()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = CreateProfilesSection();
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<ShellProfileItemViewModel>();
		first.IsItemDirty.ShouldBeFalse();
		first.TabHeader.ShouldNotContain('•');

		first.DisplayName = "Renamed";
		first.IsItemDirty.ShouldBeTrue();
		first.TabHeader.ShouldEndWith(" •");

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		first.IsItemDirty.ShouldBeFalse();
		first.TabHeader.ShouldNotContain('•');

		// A failed save (duplicate ids) must leave item dirty flags untouched.
		var second = (ShellProfileItemViewModel)section.Items[1];
		second.Id = first.Id;
		second.IsItemDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		second.IsItemDirty.ShouldBeTrue();
		second.TabHeader.ShouldEndWith(" •");
	}

	[Test]
	public async Task Duplicate_ids_block_save_with_status()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = CreateProfilesSection();
		await section.LoadAsync(CancellationToken.None);
		((ShellProfileItemViewModel)section.Items[1]).Id = ((ShellProfileItemViewModel)section.Items[0]).Id;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task Successful_save_sets_confirmation_status_with_section_label_and_item_count()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = CreateProfilesSection();
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<ShellProfileItemViewModel>();
		first.DisplayName = "Renamed";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		section.StatusText.ShouldBe($"Saved {section.Label} ({section.Items.Count} items).");
	}

	[Test]
	public async Task AddNewItem_creates_selectable_dirty_item_and_RemoveItem_deletes_node()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = CreateProfilesSection();
		await section.LoadAsync(CancellationToken.None);
		var before = section.Items.Count;
		section.AddNewItem();
		section.Items.Count.ShouldBe(before + 1);
		section.SelectedItem.ShouldBeSameAs(section.Items[^1]);
		section.IsDirty.ShouldBeTrue();
		section.RemoveItem(section.Items[^1]);
		section.Items.Count.ShouldBe(before);
	}

	[Test]
	public async Task Recent_folders_save_normalizes_and_dedups_without_dropping_entries()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new RecentFoldersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		section.FoldersText = string.Join('\n',
			Enumerable.Range(1, 25).Select(i => $@"C:\p{i}").Append(@"C:\P1").Append(""));
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonSerializer.Deserialize<string[]>(
			await File.ReadAllTextAsync(Paths.RecentDirectoriesPath))!;
		saved.Length.ShouldBe(25);
		saved.Where(s => string.Equals(s, @"C:\p1", StringComparison.OrdinalIgnoreCase))
			.ShouldHaveSingleItem();
		section.StatusText.ShouldBe($"Saved {section.Label} (25 items).");
	}

	[Test]
	public async Task Recent_directories_label_says_directories_and_AddDirectory_appends_a_line()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new RecentFoldersSectionViewModel(Store);
		section.Label.ShouldBe("Recent directories");
		await section.LoadAsync(CancellationToken.None);
		section.IsDirty.ShouldBeFalse();

		section.FoldersText = string.Empty;
		section.AddDirectory(@"C:\first");
		section.FoldersText.ShouldBe(@"C:\first");

		section.AddDirectory(@"C:\second");
		section.FoldersText.ShouldBe("C:\\first\nC:\\second");
		section.IsDirty.ShouldBeTrue();
	}

	[Test]
	public async Task PromptTemplates_load_and_save_round_trips_unknown_property_after_body_edit()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var path = Paths.PromptTemplatesPath;
		var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		node[0]!["type"] = "gitStatus";
		await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = new PromptTemplatesSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<PromptTemplateItemViewModel>();
		first.Body = "Updated body";
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		((string?)saved[0]!["body"]).ShouldBe("Updated body");
		((string?)saved[0]!["type"]).ShouldBe("prompt");
	}

	[Test]
	public async Task PromptTemplates_duplicate_ids_block_save_with_status()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new PromptTemplatesSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		((PromptTemplateItemViewModel)section.Items[1]).Id = ((PromptTemplateItemViewModel)section.Items[0]).Id;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task WebLinkTemplates_load_and_save_round_trips_unknown_field()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var path = Paths.WebLinkTemplatesPath;
		var node = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		node[0]!["openInNewTab"] = true;
		await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = new WebLinkTemplatesSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<WebLinkTemplateItemViewModel>();
		first.Title = "Renamed link";
		section.IsDirty.ShouldBeTrue();
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		((string?)saved[0]!["title"]).ShouldBe("Renamed link");
		((bool?)saved[0]!["openInNewTab"]).ShouldBe(true); // unknown field preserved
	}

	[Test]
	public async Task WebLinkTemplates_duplicate_ids_block_save_with_status()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new WebLinkTemplatesSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		((WebLinkTemplateItemViewModel)section.Items[1]).Id = ((WebLinkTemplateItemViewModel)section.Items[0]).Id;
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
	}

	[Test]
	public async Task Prompt_shell_templates_load_into_two_type_groups()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var path = Paths.PromptTemplatesPath;
		var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		json.Add(new JsonObject
		{
			["id"] = "legacy-selection",
			["name"] = "Legacy selection",
			["body"] = "Review {selectedText}",
			["sendByDefault"] = false,
			["type"] = "selectionTemplate",
			["customFlag"] = true
		});
		await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		section.Groups.Select(group => group.Label).ShouldBe(["Prompts", "Shell commands"]);
		var legacy = section.Prompts.Items.Where(item => item.Id == "legacy-selection").ShouldHaveSingleItem();
		legacy.Type.ShouldBe(PromptActionType.Prompt);
		legacy.UsesSelectedText.ShouldBeTrue();
		section.ShellCommands.Items.ShouldNotContain(item => item.Id == "legacy-selection");
	}

	[Test]
	public async Task Changing_type_moves_the_item_and_keeps_it_selected()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		var item = section.Prompts.Items[0];
		item.SendByDefault = true;
		item.Type = PromptActionType.TerminalCommand;
		section.Prompts.Items.ShouldNotContain(item);
		section.ShellCommands.Items.ShouldContain(item);
		section.SelectedGroup.ShouldBeSameAs(section.ShellCommands);
		section.ShellCommands.SelectedItem.ShouldBeSameAs(item);
		item.SendByDefault.ShouldBeTrue();
		section.IsDirty.ShouldBeTrue();
	}

	[Test]
	public async Task Adding_in_shell_group_and_saving_writes_canonical_type_and_preserves_unknown_fields()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		var existing = section.Prompts.Items[0];
		existing.Node["customFlag"] = true;
		existing.Body = "Updated body";
		var added = section.AddNewTemplate(PromptActionType.TerminalCommand);
		added.Id = "shell-with-selection";
		added.Name = "Shell with selection";
		added.Body = "rg --fixed-strings -- {selectedText}";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(Paths.PromptTemplatesPath))!.AsArray();
		var savedExisting = saved.Single(node => (string?)node!["id"] == existing.Id)!.AsObject();
		var savedAdded = saved.Single(node => (string?)node!["id"] == added.Id)!.AsObject();
		((bool?)savedExisting["customFlag"]).ShouldBe(true);
		((string?)savedExisting["type"]).ShouldBe("prompt");
		((string?)savedAdded["type"]).ShouldBe("terminalCommand");
		((bool?)savedAdded["sendByDefault"]).ShouldBe(true);
		added.UsesSelectedText.ShouldBeTrue();
	}

	[Test]
	public async Task New_template_auto_submit_default_depends_on_creation_group()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		var prompt = section.AddNewTemplate(PromptActionType.Prompt);
		var shell = section.AddNewTemplate(PromptActionType.TerminalCommand);
		prompt.SendByDefault.ShouldBeFalse();
		shell.SendByDefault.ShouldBeTrue();
	}

	[Test]
	public async Task Changing_type_selects_nearest_item_in_old_group()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		var moved = section.Prompts.Items[0];
		var remaining = section.Prompts.Items[1];
		section.Prompts.SelectedItem = moved;

		moved.Type = PromptActionType.TerminalCommand;

		section.Prompts.SelectedItem.ShouldBeSameAs(remaining);
		section.ShellCommands.SelectedItem.ShouldBeSameAs(moved);
	}
	[Test]
	public async Task PromptTemplates_remove_through_specialized_api_updates_group_and_master_list()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		PromptTemplatesSectionViewModel section = new(Store);
		await section.LoadAsync(CancellationToken.None);
		var removed = section.ShellCommands.Items[0];
		section.RemoveTemplate(removed);
		section.ShellCommands.Items.ShouldNotContain(removed);
		section.Items.ShouldNotContain(removed);
	}
}

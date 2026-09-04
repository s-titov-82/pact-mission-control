using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Git;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

/// <summary>The Buttons tab of the Git popup section (git-helpers.json "commands").</summary>
public sealed class GitCommandsSectionTests : IDisposable
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

	private async Task<GitHelpersSectionViewModel> LoadSectionAsync()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		return section;
	}

	[Test]
	public async Task Load_shows_all_builtin_command_tabs_on_the_buttons_collection()
	{
		var section = await LoadSectionAsync();

		var commands = section.CommandItems.OfType<GitCommandItemViewModel>().ToList();
		commands.Count.ShouldBe(GitButtonCommandSet.Defaults.Count);
		section.CommandItems.Count.ShouldBe(commands.Count); // no stray helper/unrecognized items here
		commands[0].Id.ShouldBe("pull");
		commands[0].IsBuiltIn.ShouldBeTrue();
		commands[0].IsDialog.ShouldBeFalse();
		commands.Single(command => command.Id == "push").IsDialog.ShouldBeTrue();
	}

	[Test]
	public async Task Editing_pull_command_and_rebase_extra_flags_round_trips_to_file()
	{
		var section = await LoadSectionAsync();
		var commands = section.CommandItems.OfType<GitCommandItemViewModel>().ToList();
		commands.Single(command => command.Id == "pull").Command = "pull --rebase --autostash";
		commands.Single(command => command.Id == "rebase").ExtraArgs = "--autostash";
		section.IsDirty.ShouldBeTrue();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var document = JsonSerializer.Deserialize<GitHelpersDocument>(
			await File.ReadAllTextAsync(FilePath), SettingsFileStore.JsonOptions)!;
		document.Commands!.Single(record => record.Id == "pull").Command.ShouldBe("pull --rebase --autostash");
		document.Commands!.Single(record => record.Id == "rebase").ExtraArgs.ShouldBe("--autostash");
	}

	[Test]
	public async Task Missing_builtin_entry_is_backfilled_with_its_default()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var root = JsonNode.Parse(await File.ReadAllTextAsync(FilePath))!.AsObject();
		var commands = root["commands"]!.AsArray();
		var pullNode = commands.OfType<JsonObject>().Single(node => (string?)node["id"] == "pull");
		commands.Remove(pullNode);
		await File.WriteAllTextAsync(FilePath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

		var section = new GitHelpersSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);

		var pull = section.CommandItems.OfType<GitCommandItemViewModel>().Single(command => command.Id == "pull");
		pull.Command.ShouldBe("pull --no-rebase");
		pull.Enabled.ShouldBeTrue();
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task AddNewCommand_creates_custom_deletable_entry_but_builtins_are_not_removable()
	{
		var section = await LoadSectionAsync();
		var before = section.CommandItems.Count;

		section.AddNewCommand();
		var custom = section.SelectedCommandItem.ShouldBeOfType<GitCommandItemViewModel>();
		custom.IsCustom.ShouldBeTrue();
		section.IsDirty.ShouldBeTrue();
		// Inserted at the end of the command tabs.
		section.CommandItems[^1].ShouldBeSameAs(custom);

		section.RemoveItem(custom);
		section.CommandItems.Count.ShouldBe(before);

		var builtIn = section.CommandItems.OfType<GitCommandItemViewModel>().First();
		section.RemoveItem(builtIn);
		section.CommandItems.ShouldContain(builtIn);
	}

	[Test]
	public async Task Custom_command_saves_all_fields_and_disabling_a_builtin_round_trips()
	{
		var section = await LoadSectionAsync();
		section.AddNewCommand();
		var custom = (GitCommandItemViewModel)section.SelectedCommandItem!;
		custom.Id = "fetch-prune";
		custom.Label = "Fetch";
		custom.Command = "fetch --prune";
		custom.Description = "Prunes gone branches.";
		custom.DocUrl = "https://git-scm.com/docs/git-fetch";
		section.CommandItems.OfType<GitCommandItemViewModel>().Single(command => command.Id == "merge").Enabled = false;

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var document = JsonSerializer.Deserialize<GitHelpersDocument>(
			await File.ReadAllTextAsync(FilePath), SettingsFileStore.JsonOptions)!;
		var saved = document.Commands!.Single(record => record.Id == "fetch-prune");
		saved.Label.ShouldBe("Fetch");
		saved.Command.ShouldBe("fetch --prune");
		saved.Description.ShouldBe("Prunes gone branches.");
		saved.DocUrl.ShouldBe("https://git-scm.com/docs/git-fetch");
		document.Commands!.Single(record => record.Id == "merge").Enabled.ShouldBeFalse();
	}

	[Test]
	[TestCase("", "needs a label")]
	[TestCase("Pull", "well-quoted command")]
	public async Task Invalid_command_blocks_save_with_status(string label, string expectedFragment)
	{
		ArgumentNullException.ThrowIfNull(label);
		ArgumentNullException.ThrowIfNull(expectedFragment);
		var section = await LoadSectionAsync();
		var pull = section.CommandItems.OfType<GitCommandItemViewModel>().Single(command => command.Id == "pull");
		pull.Label = label;
		pull.Command = label.Length == 0 ? pull.Command : "pull \"broken";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.StatusText!.ShouldContain(expectedFragment);
	}

	[Test]
	public async Task Duplicate_and_unbalanced_dialog_flags_block_save()
	{
		var section = await LoadSectionAsync();
		section.AddNewCommand();
		var custom = (GitCommandItemViewModel)section.SelectedCommandItem!;
		custom.Id = "pull";
		custom.Label = "Dup";
		custom.Command = "pull";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.StatusText!.ShouldContain("unique");

		custom.Id = "unique-id";
		section.CommandItems.OfType<GitCommandItemViewModel>().Single(command => command.Id == "merge").ExtraArgs = "\"broken";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.StatusText!.ShouldContain("unbalanced quotes");
	}

	[Test]
	public async Task MoveSelectedCommand_reorders_the_tab_strip_and_persists_the_new_order_on_save()
	{
		var section = await LoadSectionAsync();
		var idsBefore = section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList();
		var pull = section.CommandItems.OfType<GitCommandItemViewModel>().Single(command => command.Id == "pull");
		section.SelectedCommandItem = pull;
		section.IsDirty.ShouldBeFalse();

		section.MoveSelectedCommand(1); // pull swaps with whatever follows it

		var idsAfter = section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList();
		idsAfter[0].ShouldBe(idsBefore[1]);
		idsAfter[1].ShouldBe(idsBefore[0]);
		section.CommandItems[1].ShouldBeSameAs(pull);
		section.SelectedCommandItem.ShouldBeSameAs(pull);
		section.CanMoveSelectedCommandLeft.ShouldBeTrue();
		section.IsDirty.ShouldBeTrue();

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var document = JsonSerializer.Deserialize<GitHelpersDocument>(
			await File.ReadAllTextAsync(FilePath), SettingsFileStore.JsonOptions)!;
		var savedOrder = document.Commands!.Select(record => record.Id).ToList();
		savedOrder.ShouldBe(idsAfter);
	}

	[Test]
	public async Task MoveSelectedCommand_left_from_the_first_slot_is_a_no_op()
	{
		var section = await LoadSectionAsync();
		section.SelectedCommandItem = section.CommandItems[0];
		section.CanMoveSelectedCommandLeft.ShouldBeFalse();
		section.CanMoveSelectedCommandRight.ShouldBeTrue();
		var idsBefore = section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList();

		section.MoveSelectedCommand(-1);

		section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList().ShouldBe(idsBefore);
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task MoveSelectedCommand_right_from_the_last_slot_is_a_no_op()
	{
		var section = await LoadSectionAsync();
		section.SelectedCommandItem = section.CommandItems[^1];
		section.CanMoveSelectedCommandLeft.ShouldBeTrue();
		section.CanMoveSelectedCommandRight.ShouldBeFalse();
		var idsBefore = section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList();

		section.MoveSelectedCommand(1);

		section.CommandItems.OfType<GitCommandItemViewModel>().Select(command => command.Id).ToList().ShouldBe(idsBefore);
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task MoveSelectedCommand_with_nothing_selected_is_a_no_op()
	{
		var section = await LoadSectionAsync();
		section.SelectedCommandItem = null;

		section.MoveSelectedCommand(1);

		section.IsDirty.ShouldBeFalse();
	}
}

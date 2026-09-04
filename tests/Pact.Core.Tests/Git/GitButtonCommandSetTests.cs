using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class GitButtonCommandSetTests
{
	[Test]
	public void Default_set_uses_builtin_commands()
	{
		var set = GitButtonCommandSet.Create(null);

		set.Arguments(GitButtonCommandSet.PullId).ShouldBe(["pull", "--no-rebase"]);
		set.Arguments(GitButtonCommandSet.StashId).ShouldBe(["stash", "push"]);
		set.Arguments(GitButtonCommandSet.StashPopId).ShouldBe(["stash", "pop"]);
		set.ExtraArguments(GitButtonCommandSet.PushId).ShouldBeEmpty();
		set.IsEnabled(GitButtonCommandSet.MergeId).ShouldBeTrue();
		set.CustomCommands.ShouldBeEmpty();
	}

	[Test]
	public void Configured_command_overrides_builtin_default()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("pull", "Pull", Command: "git pull --rebase --autostash")]);

		set.Arguments("pull").ShouldBe(["pull", "--rebase", "--autostash"]);
	}

	[Test]
	public void Blank_or_broken_command_falls_back_to_builtin_default()
	{
		var set = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("pull", "Pull", Command: "   "),
			new GitButtonCommandRecord("stash", "Stash", Command: "stash \"broken")
		]);

		set.Arguments("pull").ShouldBe(["pull", "--no-rebase"]);
		set.Arguments("stash").ShouldBe(["stash", "push"]);
	}

	[Test]
	public void Extra_arguments_come_from_dialog_entries()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("merge", "Merge", ExtraArgs: "--no-ff")]);

		set.ExtraArguments("merge").ShouldBe(["--no-ff"]);
		set.ExtraArguments("rebase").ShouldBeEmpty();
	}

	[Test]
	public void Disabled_entry_reports_disabled_but_unknown_id_reports_enabled()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("merge", "Merge", Enabled: false)]);

		set.IsEnabled("merge").ShouldBeFalse();
		set.IsEnabled("pull").ShouldBeTrue();
	}

	[Test]
	public void Custom_entries_surface_with_parsed_arguments()
	{
		var set = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune"),
			new GitButtonCommandRecord("disabled", "Off", Command: "gc", Enabled: false),
			new GitButtonCommandRecord("blank", "Blank", Command: "  ")
		]);

		var custom = set.CustomCommands.ShouldHaveSingleItem();
		custom.Label.ShouldBe("Fetch");
		custom.Arguments.ShouldBe(["fetch", "--prune"]);
	}

	[Test]
	public void Builtin_ids_are_classified()
	{
		GitButtonCommandSet.IsBuiltInId("pull").ShouldBeTrue();
		GitButtonCommandSet.IsDialogId("push").ShouldBeTrue();
		GitButtonCommandSet.IsDialogId("pull").ShouldBeFalse();
		GitButtonCommandSet.IsBuiltInId("fetch-prune").ShouldBeFalse();
	}

	[Test]
	public void PopupButtons_follow_file_order_with_custom_entries_interleaved()
	{
		var set = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("push", "Push"),
			new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune"),
			new GitButtonCommandRecord("pull", "Pull", Command: "pull --no-rebase")
		]);

		// The three configured entries keep their file order; missing built-ins (stash, stash-pop,
		// commit, switch, rebase, merge) are appended after them, so only check the head.
		set.PopupButtons.Take(3).Select(button => button.Id).ShouldBe(["push", "fetch-prune", "pull"]);
	}

	[Test]
	public void PopupButtons_marks_custom_entry_kind_and_carries_arguments()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune")]);

		var button = set.PopupButtons.Where(candidate => candidate.Kind == GitPopupButtonKind.Custom).ShouldHaveSingleItem();
		button.Id.ShouldBe("fetch-prune");
		button.Label.ShouldBe("Fetch");
		button.CustomArguments.ShouldBe(["fetch", "--prune"]);
	}

	[Test]
	public void PopupButtons_appends_missing_builtins_in_defaults_order()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("push", "Push")]);

		set.PopupButtons[0].Id.ShouldBe("push");
		IReadOnlyList<string> tail = set.PopupButtons.Skip(1).Select(button => button.Id).ToList();
		IReadOnlyList<string> expectedTail = GitButtonCommandSet.Defaults
			.Select(record => record.Id)
			.Where(id => id != "push")
			.ToList();
		tail.ShouldBe(expectedTail);
	}

	[Test]
	public void PopupButtons_excludes_disabled_builtins()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("merge", "Merge", Enabled: false)]);

		set.PopupButtons.ShouldNotContain(button => button.Id == "merge");
	}

	[Test]
	public void PopupButtons_skips_disabled_and_unparseable_custom_entries()
	{
		var set = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("disabled", "Off", Command: "gc", Enabled: false),
			new GitButtonCommandRecord("broken", "Broken", Command: "stash \"broken"),
			new GitButtonCommandRecord("blank", "Blank", Command: "  "),
			new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune")
		]);

		set.PopupButtons.Where(button => button.Kind == GitPopupButtonKind.Custom).Select(button => button.Id).ShouldBe(["fetch-prune"]);
	}

	[Test]
	public void PopupButtons_uses_default_label_when_builtin_label_is_blank()
	{
		var set = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("pull", "   ", Command: "pull --no-rebase")]);

		var pull = set.PopupButtons.Where(button => button.Id == "pull").ShouldHaveSingleItem();
		pull.Label.ShouldBe("Pull");
	}

	[Test]
	public void Defaults_cover_all_builtin_ids_with_labels_descriptions_and_doc_links()
	{
		foreach (var record in GitButtonCommandSet.Defaults)
		{
			GitButtonCommandSet.IsBuiltInId(record.Id).ShouldBeTrue();
			string.IsNullOrWhiteSpace(record.Label).ShouldBeFalse();
			string.IsNullOrWhiteSpace(record.Description).ShouldBeFalse();
			record.DocUrl.ShouldStartWith("https://git-scm.com/docs/");

			if (GitButtonCommandSet.IsDialogId(record.Id))
			{
				record.Command.ShouldBeNull();
				string.IsNullOrWhiteSpace(GitButtonCommandSet.DialogPreview(record.Id)).ShouldBeFalse();
			}
			else
			{
				string.IsNullOrWhiteSpace(record.Command).ShouldBeFalse();
			}
		}
	}
}
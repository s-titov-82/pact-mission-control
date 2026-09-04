namespace Pact.Core.Git;

/// <summary>
/// One entry of the git popup's "commands" settings array. Simple built-ins and custom entries
/// carry <paramref name="Command"/> (the full argument string); dialog built-ins carry
/// <paramref name="ExtraArgs"/> (fixed flags inserted after the subcommand, the rest of the
/// command stays dialog-generated).
/// </summary>
public sealed record GitButtonCommandRecord(
	string Id,
	string Label,
	string? Command = null,
	string? ExtraArgs = null,
	bool Enabled = true,
	string? Description = null,
	string? DocUrl = null);

/// <summary>A custom (user-added) popup button: its label and already-split git arguments.</summary>
public sealed record GitCustomCommand(string Label, IReadOnlyList<string> Arguments);

/// <summary>How a popup button dispatches its click: a fixed argument list, a dialog-driven flow,
/// or a user-added custom command.</summary>
public enum GitPopupButtonKind
{
	/// <summary>Runs a fixed argument list straight from settings.</summary>
	Simple,

	/// <summary>Opens a dialog that composes the command; settings supply only extra flags.</summary>
	Dialog,

	/// <summary>Runs a user-added command carried on the descriptor itself.</summary>
	Custom
}

/// <summary>
/// One popup button in render order: id/label/kind for a data-driven button row. Custom entries
/// carry their already-split <see cref="CustomArguments"/> so the click handler can run them
/// directly without looking the id back up.
/// </summary>
public sealed record GitPopupButtonDescriptor(
	string Id,
	string Label,
	GitPopupButtonKind Kind,
	IReadOnlyList<string>? CustomArguments = null);

/// <summary>
/// The configured git popup button commands: loaded records over built-in defaults. A missing,
/// blank, or unparseable entry always falls back to the built-in default, so a stripped-down or
/// hand-broken settings file never breaks a popup button.
/// </summary>
public sealed class GitButtonCommandSet
{
	/// <summary>Built-in id for the pull button.</summary>
	public const string PullId = "pull";

	/// <summary>Built-in id for the stash button.</summary>
	public const string StashId = "stash";

	/// <summary>Built-in id for the stash-pop button.</summary>
	public const string StashPopId = "stash-pop";

	/// <summary>Built-in id for the commit button.</summary>
	public const string CommitId = "commit";

	/// <summary>Built-in id for the push button.</summary>
	public const string PushId = "push";

	/// <summary>Built-in id for the branch-switch button.</summary>
	public const string SwitchId = "switch";

	/// <summary>Built-in id for the rebase button.</summary>
	public const string RebaseId = "rebase";

	/// <summary>Built-in id for the merge button.</summary>
	public const string MergeId = "merge";

	private static readonly string[] SimpleIds = [PullId, StashId, StashPopId];
	private static readonly string[] DialogIds = [CommitId, PushId, SwitchId, RebaseId, MergeId];

	private static readonly Dictionary<string, string> DialogPreviews = new(StringComparer.Ordinal)
	{
		[CommitId] = "git commit [extra flags] -m <message> -- <files>",
		[PushId] = "git push [extra flags] [--force-with-lease|--force] [-u] <remote> <branch>",
		[SwitchId] = "git switch [extra flags] [-c|--track] <branch>",
		[RebaseId] = "git rebase [extra flags] <branch>",
		[MergeId] = "git merge [extra flags] <branch>"
	};

	/// <summary>
	/// Built-in command records used when settings omit an entry or supply an unusable one.
	/// </summary>
	public static IReadOnlyList<GitButtonCommandRecord> Defaults { get; } =
	[
		new GitButtonCommandRecord(
			PullId,
			"Pull",
			Command: "pull --no-rebase",
			Description: "--no-rebase: merge when local and remote diverged (avoids 'Not possible to fast-forward'). Alternatives: --rebase replays local commits on top; --ff-only refuses to pull diverged branches. Add --autostash to stash/restore dirty files automatically.",
			DocUrl: "https://git-scm.com/docs/git-pull"),
		new GitButtonCommandRecord(
			StashId,
			"Stash",
			Command: "stash push",
			Description: "Add -u/--include-untracked to stash untracked files too; -m \"text\" names the stash entry.",
			DocUrl: "https://git-scm.com/docs/git-stash"),
		new GitButtonCommandRecord(
			StashPopId,
			"Pop stash",
			Command: "stash pop",
			Description: "Applies the newest stash entry and drops it on success. Use 'stash apply' instead to keep the entry.",
			DocUrl: "https://git-scm.com/docs/git-stash"),
		new GitButtonCommandRecord(
			CommitId,
			"Commit",
			Description: "Dialog button: the message and file list come from the commit dialog. Useful extra flags: --signoff adds a Signed-off-by trailer; --no-verify skips commit hooks.",
			DocUrl: "https://git-scm.com/docs/git-commit"),
		new GitButtonCommandRecord(
			PushId,
			"Push",
			Description: "Dialog button: remote, branch, force mode, and -u come from the push dialog. Useful extra flags: --follow-tags pushes annotated tags reachable from the branch; --tags pushes all tags.",
			DocUrl: "https://git-scm.com/docs/git-push"),
		new GitButtonCommandRecord(
			SwitchId,
			"Switch",
			Description: "Dialog button: the branch (and -c/--track) comes from the branch picker.",
			DocUrl: "https://git-scm.com/docs/git-switch"),
		new GitButtonCommandRecord(
			RebaseId,
			"Rebase",
			Description: "Dialog button: the target branch comes from the branch picker. Useful extra flags: --autostash stashes/restores dirty files around the rebase; --update-refs also moves stacked branch refs.",
			DocUrl: "https://git-scm.com/docs/git-rebase"),
		new GitButtonCommandRecord(
			MergeId,
			"Merge",
			Description: "Dialog button: the branch to merge comes from the branch picker. Useful extra flags: --no-ff always creates a merge commit; --squash stages the result without committing.",
			DocUrl: "https://git-scm.com/docs/git-merge")
	];

	private readonly IReadOnlyDictionary<string, GitButtonCommandRecord> _records;

	private GitButtonCommandSet(
		IReadOnlyDictionary<string, GitButtonCommandRecord> records,
		IReadOnlyList<GitCustomCommand> customCommands,
		IReadOnlyList<GitPopupButtonDescriptor> popupButtons)
	{
		_records = records;
		CustomCommands = customCommands;
		PopupButtons = popupButtons;
	}

	/// <summary>
	/// Whether <paramref name="id"/> names a built-in button. Ids that are not built-in are
	/// treated as user-added custom commands.
	/// </summary>
	public static bool IsBuiltInId(string id) =>
		SimpleIds.Contains(id, StringComparer.Ordinal) || DialogIds.Contains(id, StringComparer.Ordinal);

	/// <summary>
	/// Whether <paramref name="id"/> names a dialog-driven built-in, whose settings entry
	/// contributes only extra flags rather than a whole command.
	/// </summary>
	public static bool IsDialogId(string id) => DialogIds.Contains(id, StringComparer.Ordinal);

	/// <summary>Read-only shape of a dialog button's generated command, for the settings form.</summary>
	public static string DialogPreview(string id) =>
		DialogPreviews.TryGetValue(id, out var preview) ? preview : string.Empty;

	/// <summary>
	/// Builds the effective command set by layering loaded records over the built-in defaults.
	/// </summary>
	/// <param name="records">
	/// Records from settings, or <see langword="null"/> to use defaults only. Entries with a
	/// blank or duplicate id are ignored, and any built-in left unconfigured keeps its default,
	/// so a partial or damaged settings file still yields a complete, working button set.
	/// </param>
	public static GitButtonCommandSet Create(IEnumerable<GitButtonCommandRecord>? records)
	{
		Dictionary<string, GitButtonCommandRecord> byId = new(StringComparer.Ordinal);
		List<GitCustomCommand> customCommands = [];
		List<GitPopupButtonDescriptor> popupButtons = [];
		HashSet<string> seenBuiltInIds = new(StringComparer.Ordinal);

		foreach (var record in records ?? [])
		{
			if (string.IsNullOrWhiteSpace(record.Id) || !byId.TryAdd(record.Id, record))
			{
				continue;
			}

			if (IsBuiltInId(record.Id))
			{
				seenBuiltInIds.Add(record.Id);
				if (!record.Enabled)
				{
					continue;
				}

				var defaultRecord = Defaults.FirstOrDefault(candidate => candidate.Id == record.Id);
				var builtInLabel = string.IsNullOrWhiteSpace(record.Label)
					? defaultRecord?.Label ?? record.Id
					: record.Label;
				popupButtons.Add(new GitPopupButtonDescriptor(
					record.Id,
					builtInLabel,
					IsDialogId(record.Id) ? GitPopupButtonKind.Dialog : GitPopupButtonKind.Simple));
				continue;
			}

			if (record.Enabled
				&& GitCommandLine.TrySplit(record.Command, out var arguments)
				&& arguments.Count > 0)
			{
				var customLabel = string.IsNullOrWhiteSpace(record.Label) ? record.Id : record.Label;
				customCommands.Add(new GitCustomCommand(customLabel, arguments));
				popupButtons.Add(new GitPopupButtonDescriptor(record.Id, customLabel, GitPopupButtonKind.Custom, arguments));
			}
		}

		foreach (var defaultRecord in Defaults)
		{
			if (seenBuiltInIds.Contains(defaultRecord.Id))
			{
				continue;
			}

			popupButtons.Add(new GitPopupButtonDescriptor(
				defaultRecord.Id,
				defaultRecord.Label,
				IsDialogId(defaultRecord.Id) ? GitPopupButtonKind.Dialog : GitPopupButtonKind.Simple));
		}

		return new GitButtonCommandSet(byId, customCommands, popupButtons);
	}

	/// <summary>User-added entries rendered as extra popup buttons, in file order.</summary>
	public IReadOnlyList<GitCustomCommand> CustomCommands { get; }

	/// <summary>
	/// All popup buttons (built-in and custom, interleaved) in exactly the "commands" array's file
	/// order: disabled built-ins are hidden, unparseable/disabled custom entries are skipped, and
	/// built-ins missing from the file are appended at the end in <see cref="Defaults"/> order.
	/// </summary>
	public IReadOnlyList<GitPopupButtonDescriptor> PopupButtons { get; }

	/// <summary>
	/// The full argument list for a simple id ("pull", "stash", "stash-pop"): the configured
	/// command when it parses to something, otherwise the built-in default.
	/// </summary>
	public IReadOnlyList<string> Arguments(string id)
	{
		if (_records.TryGetValue(id, out var record)
			&& GitCommandLine.TrySplit(record.Command, out var arguments)
			&& arguments.Count > 0)
		{
			return arguments;
		}

		var fallback = Defaults.FirstOrDefault(candidate => candidate.Id == id);
		return fallback is null ? [] : GitCommandLine.Split(fallback.Command);
	}

	/// <summary>Fixed extra flags for a dialog id; empty when unset or unparseable.</summary>
	public IReadOnlyList<string> ExtraArguments(string id)
	{
		if (_records.TryGetValue(id, out var record)
			&& GitCommandLine.TrySplit(record.ExtraArgs, out var arguments))
		{
			return arguments;
		}

		return [];
	}

	/// <summary>False only when the entry exists and was explicitly disabled.</summary>
	public bool IsEnabled(string id) =>
		!_records.TryGetValue(id, out var record) || record.Enabled;
}
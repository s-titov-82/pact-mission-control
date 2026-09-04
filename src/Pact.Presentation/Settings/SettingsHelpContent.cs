namespace Pact.Presentation.Settings;

/// <summary>
/// Per-section help text consumed by the desktop settings help view. Bodies are plain text
/// (blank-line paragraphs, simple "- " bullets) and are shown as-is in a read-only wrapped text
/// box, with no markdown rendering. Kept in sync manually with the public EN mirror under
/// docs/help/en/ (docs/help/ru/ carries Russian translations of the same text, for future
/// language switching; neither doc tree is read by the app).
/// </summary>
public static class SettingsHelpContent
{
	/// <summary>
	/// Returns the help title and body for a section. Every section has an entry, so this never
	/// returns blank text.
	/// </summary>
	public static (string Title, string Body) Get(SettingsSection section) => section switch
	{
		SettingsSection.RootTabs => ("Root tabs", RootTabsBody),
		SettingsSection.Projects => ("Current projects", ProjectsBody),
		SettingsSection.PausedProjects => ("Paused projects", PausedProjectsBody),
		SettingsSection.LaunchProfiles => ("Terminal templates", LaunchProfilesBody),
		SettingsSection.ReviewProfiles => ("Review profiles", ReviewProfilesBody),
		SettingsSection.Orchestrator => ("Orchestrator", OrchestratorBody),
		SettingsSection.PromptTemplates => ("Prompt/Shell templates", PromptTemplatesBody),
		SettingsSection.WebLinkTemplates => ("Web link templates", WebLinkTemplatesBody),
		SettingsSection.WebMonitoringRules => ("Web monitoring rules", WebMonitoringRulesBody),
		SettingsSection.Scenarios => ("Scenarios", ScenariosBody),
		SettingsSection.GitHelpers => ("Git popup", GitHelpersBody),
		SettingsSection.RecentFolders => ("Recent directories", RecentDirectoriesBody),
		SettingsSection.Appearance => ("Appearance", AppearanceBody),
		_ => throw new ArgumentOutOfRangeException(nameof(section), section, "Unknown settings section.")
	};

	private const string AppearanceBody = "Choose System to follow Windows, or force the Light or Dark application theme. You can independently hide the selected-tab diagnostic facts shown below Quick actions and opt in to external process metrics. External process metrics sampling stays off while that option is disabled. Saved choices apply immediately.";

	private const string RootTabsBody = """
        ROOT holds terminal and browser tabs that are not owned by any project. Their definitions, last selected item, and individual pause states are stored in root-tabs.json.

        Terminal title, working directory, launch command, and resume command are editable here. Unlike project agent sessions, every ROOT terminal has an explicit working directory; newly created ROOT terminals start in the existing Windows user profile directory.

        Browser title and URL are editable here. Changing the URL affects the saved address used the next time the page is loaded.

        Pause and Resume remain per-row actions in the main window. Selecting a paused row does not resume it.
        """;

	private const string ProjectsBody = """
        Each tab is one currently open (active) project. Editing name, root path, notes, GitLab repo id, or TeamCity project id here applies immediately to the running app — there is no separate publish step for other windows to see the change.

        Root path applies to future session launches and to the git panel; a session that is already running keeps the working directory it started with.

        The read-only summary line above the fields (id, status, created/active timestamps) reflects the project's current runtime state and cannot be edited here.

        Every session that belongs to the project is listed under it. A session's title, working directory, launch command, and resume command are editable — except while the session is locked by a running scenario, when a "Locked by a running scenario" hint appears and its fields become read-only until the run finishes. The working directory field is hidden for agent sessions (Codex, Claude, Hermes), which always run in the project's own directory; it only applies to pwsh/custom shell sessions.

        There is no delete button for a project in this section. Projects are created by opening a directory from the main window and removed by closing the project there — Settings only edits the projects that are currently open. A paused project is edited from the separate "Paused projects" section instead of here.
        """;

	private const string PausedProjectsBody = """
        Each tab is one paused project — a project whose sessions were closed and set aside, but that has not been removed. Editing works exactly like Current projects: name, root path, notes, GitLab repo id, TeamCity project id, and each session's title, working directory, launch command, and resume command all apply immediately to the running app.

        There is no "+" here: adding a project from a directory always creates an active project, so use the Current projects section (or the main window) for that. Unpausing/resuming a project is also done from the main window, not from Settings.

        Everything else — the read-only summary line, the locked-session hint, the hidden working-directory field for agent sessions, and the lack of a delete button — behaves the same as in Current projects.
        """;

	private const string LaunchProfilesBody = """
        Each tab is one launch profile; every profile becomes one launch button in the main window.

        Command is the command that starts a brand-new session for this profile. Resume command is copied into a session as its initial resume command when that session is created, but it is not consulted again after that: if a session's own resume command text is not in a recognized shape (for example "codex resume <id>" or "claude --resume <id>"), restoring the session falls back to the session's own launch command instead.

        Pact preserves the selected profile command and appends session-scoped guidance for Codex and Claude. Claude receives it through inline --append-system-prompt; Codex receives an invocation-level developer_instructions value, which overrides any value for the same key inherited from a selected Codex config profile.

        Id, Command, and Shell must all be non-empty, and ids must be unique across profiles — Save section rejects the whole file and names the offending profile otherwise.
        """;

	private const string ReviewProfilesBody = """
        Each tab is a reviewer-only launch profile used when an agent asks Pact to start a review. These profiles do not appear in the normal project launch menu.

        Command carries the model and effort flags for the reviewer. Pact tools are connected automatically for the Claude and Codex kinds, using the arguments each command-line interface accepts; no per-profile configuration is involved.

        Connected agents are notified to refresh their review profile ids and scenario ids whenever a Settings reload changes the live catalog, including when malformed scenario JSON activates the built-in defaults. External file edits take effect only at the Settings reload boundary or after the application restarts.

        Id and Command must be non-empty, and ids must be unique. Unknown JSON fields are preserved when the section is saved.
        """;

	private const string OrchestratorBody = """
        The orchestrator is one dedicated Hermes session pinned above projects and ROOT. It can inspect session status, read retained agent messages, report subscription usage and active review runs, and send a prompt to another live session when that session is not controlled by a scenario.

        Initialize asks Hermes to create the Pact profile, installs the Pact MCP connection, SOUL and status-report skill, writes the endpoint and credential to the profile environment, and then saves the launch configuration. Every provisioning step is shown separately. Existing Hermes configuration is preserved semantically, and Pact backs up files before replacing content it does not recognize as its own.

        Reissue credential invalidates the stored orchestrator credential only after the Hermes profile has been updated successfully. Save section persists the enabled switch, workstation lock detection switch, and both prompt texts. Lock detection remains separate from enabling the slot.
        """;

	private const string PromptTemplatesBody = """
        This catalog has two groups: Prompt templates target agent sessions (Codex, Claude, Hermes); Shell command templates target Pwsh and Custom sessions. Type controls the compatible target kind only.

        A body containing the exact {selectedText} token is selection-aware regardless of Type. Static templates appear in Quick actions. Selection-aware templates appear in Send selection to, where the selected template filters the visible target sessions. Selected text is substituted verbatim; shell commands are not automatically quoted.

        Auto-submit independently decides whether Enter is pressed after insertion for either Prompt or Shell command. Raw selection never submits. New Prompt items default Auto-submit off; new Shell command items default it on. Changing Type preserves the checkbox. The persisted JSON property is sendByDefault.

        Available placeholders are {project}, {task}, {selectedText}, and {otherSessionSummary}. Auto-submit is no longer a no runtime effect setting: it controls Enter for both types. Unknown JSON fields are preserved. Legacy selectionTemplate values remain readable and normalize to Prompt.
        """;

	private const string WebLinkTemplatesBody = """
        Each tab is one web link template; every template becomes one menu entry in the "@" menus for both a project and ROOT in the main window.

        Clicking a template's menu entry renders its start URL against the project's settings and opens the result as a new web page tab in the app. The %gitLabRepoId% and %teamCityProjectId% placeholders are substituted from the project's GitLab repo id and TeamCity project id (edited in the Projects section); whenever either one resolves blank on the project, the whole rendered URL is discarded — unconditionally, not only when nothing else is left to render — and the template's site root (scheme and host only) opens instead. Any other %placeholder% name makes the whole template fail with an error message instead of opening a page.

        From ROOT, a template without placeholders opens its exact URL. ROOT has no project ids, so a template using %gitLabRepoId% or %teamCityProjectId% falls back to that template's site root.

        The rendered URL is captured once, at the moment the web page is created — it does not update later if the project's ids change. Open a new web page from the template to pick up new values.

        Start URL must be an absolute http or https URL (after substitution, or as the site-root fallback), or the click fails with an error instead of opening a page.
        """;

	private const string WebMonitoringRulesBody = """
        Each rule matches loaded web tabs with its URL pattern and polls the page through declarative DOM extractors. Rules are checked in file order; the first enabled matching rule owns that tab.

        The optional Activity extractor reports whether work is currently in progress. The optional Revision extractor reads a stable value whose change can mark a background tab unread. Each extractor uses a CSS selector and a source (exists, count, text, or attribute); text and attribute sources can apply a regular expression and capture group.

        The two starter rules are disabled examples. Their CHANGE-ME- hosts use the reserved .invalid domain and cannot be enabled unchanged. Replace each host and verify the selectors against your own authenticated TeamCity or GitLab pages before enabling the rule.

        Test on current tab evaluates the edited rule once against the currently loaded web tab and reports the URL match, activity, revision, or error. Testing does not save the section, clear unsaved edits, or change live monitoring state.
        """;

	private const string ScenariosBody = """
        A scenario automates a review loop between two live terminal sessions: an author and a reviewer. You start one from the Scenarios list in the main window by clicking its button; a setup dialog then binds the author and reviewer roles to actual running sessions and lets you set the review target (a scope pointer or pasted text) before the run starts.

        Every scenario definition here is a "review-loop": the reviewer checks the author's work against a stop marker across up to Max iterations passes, using four prompt templates in a fixed order:

        - Start prompt template — sent to the reviewer for pass 1; carries the full review brief (target, criteria, marker rules).
        - First feedback template — sent to the author after pass 1, carrying the reviewer's findings.
        - Author return template — sent to the reviewer for pass 2 through N, carrying the author's reply for re-verification.
        - Feedback template — sent to the author for pass 2 through N, carrying the reviewer's follow-up findings.

        Completion is decided from the footer-complete reviewer response file: whenever that response contains the exact Stop marker text, the run ends successfully — there is no other automatic success condition. Terminal screen state or captured output alone never completes a pass. If Max iterations passes elapse without the marker appearing, the run stops incomplete.

        Reviewer instructions are free-form text presets, not a fixed list of disciplines; add or remove them with the +/− buttons next to the list, and edit each preset's Id, Name, and Text. Default reviewer instruction picks which preset is pre-selected when you set up a run of this scenario.

        Default target seeds the setup dialog's review-target field; it can still be overridden for any individual run.

        While a run is active, both involved sessions are input-locked — you cannot type into them, though their output stays visible and scrollable. Manual Pause unlocks both sessions and blocks new automatic terminal writes until Resume; a valid response file that appears during the pause advances the run and restores the locks without requiring Resume. If watchdog attention pauses a run because a session looks stuck, only that affected session unlocks so you can answer it manually and let the run resume.

        Every run is journaled in memory while it can still be shown in the journal panel. Closing the run discards that journal; no scenario journal is written to the data root.
        """;

	private const string GitHelpersBody = """
        This section configures the project's git panel ⎇ popup, split into two top-level tabs: Buttons (what the popup's own command buttons run) and External helpers.

        Buttons tab. Simple built-in buttons (Pull, Stash, Pop stash) have an editable Command — the full git argument string, quotes group words and a leading "git " is tolerated. Dialog buttons (Commit, Push, Switch, Rebase, Merge) get their arguments from their dialogs, so only Extra flags is editable: fixed flags inserted right after the git subcommand (for example --no-ff for merge, --autostash for rebase); the read-only preview line shows where they land. Every built-in has an Enabled toggle — a disabled button is hidden in the popup — and no delete: built-ins can only be disabled, and even an entry deleted from the raw JSON silently falls back to its built-in default (the tab reappears with defaults on next load). "+" adds a custom entry: it becomes an extra button in the popup running its Command as-is, and unlike built-ins it can be deleted. The ◀/▶ buttons next to "+" move the selected tab one slot left or right — this reorders the popup's buttons too, since the tab order is the same order the buttons render in. The "Rebase onto base" scenario reuses the configured Pull command and the Rebase extra flags.

        External helpers tab. Each one is an external git GUI tool integration; its buttons appear in the same popup, but only when the tool actually resolves on this machine — a helper that does not resolve contributes no buttons and is not shown at all.

        Resolution order for Executable: an absolute path that exists on disk is used as-is; otherwise, on Windows, the Registry key/value probe is read (checking both the 64-bit and 32-bit registry views) and used if it points at a file that exists; otherwise the executable name is looked up on PATH. If none of these resolve to an existing file, the helper is hidden.

        Each popup action has a Slot that decides where it appears:

        - history — a history/log button in the popup's action list.
        - custom — an extra command button, shown alongside history in the same list.
        - resolve — the panel's conflict-resolution action; only the first action with this slot is used. When no helper defines a resolve action, "Resolve" falls back to running "git mergetool -y" directly.
        - any other slot value is not surfaced anywhere and is effectively ignored.

        Arguments (one per line) can use {root} and {branch} placeholders, substituted with the project's root path and current branch name when the action launches.

        External tools worth wiring up this way: TortoiseGit, Fork, SourceTree, GitExtensions — each has its own command-line switches for opening a log view or a conflict resolver, which you translate into an action's Arguments.
        """;

	private const string RecentDirectoriesBody = """
        This list feeds the directory suggestions shown in the new-session directory picker in the main window; it is not used anywhere else.

        On save, entries are normalized: each line is trimmed, blank lines are dropped, duplicates are removed case-insensitively, and the list is capped at 20 entries — anything beyond the first 20 after dedup is dropped. "Add directory" appends a picked folder as a new line; the same normalization still applies when you save.

        One directory path per line.
        """;
}

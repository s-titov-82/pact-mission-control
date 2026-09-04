namespace Pact.Infrastructure.Storage;

/// <summary>
/// Maps every Pact-owned file below one data root whose top-level directories have explicit
/// durability semantics.
/// </summary>
public sealed class AppPaths
{
	/// <summary>Creates the canonical path map without creating files or directories.</summary>
	public AppPaths(string rootDirectory)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

		RootDirectory = Path.GetFullPath(rootDirectory);
		SettingsDirectory = Path.Combine(RootDirectory, "Settings");
		WebViewDirectory = Path.Combine(RootDirectory, "WebView");
		LogsDirectory = Path.Combine(RootDirectory, "Logs");
		TempDirectory = Path.Combine(RootDirectory, "Temp");
		SessionTempDirectory = Path.Combine(TempDirectory, "Session");
		RetainedTempDirectory = Path.Combine(TempDirectory, "Retained");
		WebMonitorSnapshotsDirectory = Path.Combine(RetainedTempDirectory, "WebMonitoring");
		AgentControlDirectory = Path.Combine(RetainedTempDirectory, "AgentControl");
		PactSkillsDirectory = Path.Combine(RetainedTempDirectory, "PactSkills");
		PactMcpSkillPath = Path.Combine(PactSkillsDirectory, "PactMcpSkill.md");
		PactCommonSkillPath = Path.Combine(PactSkillsDirectory, "PactCommonSkill.md");
		AtomicTempDirectory = Path.Combine(SessionTempDirectory, "atomic");

		ProjectsPath = Path.Combine(SettingsDirectory, "projects.json");
		RootTabsPath = Path.Combine(SettingsDirectory, "root-tabs.json");
		ShellProfilesPath = Path.Combine(SettingsDirectory, "shell-profiles.json");
		ReviewProfilesPath = Path.Combine(SettingsDirectory, "review-profiles.json");
		AgentControlSettingsPath = Path.Combine(SettingsDirectory, "agent-control.json");
		OrchestratorPath = Path.Combine(SettingsDirectory, "orchestrator.json");
		PromptTemplatesPath = Path.Combine(SettingsDirectory, "prompt-templates.json");
		WebLinkTemplatesPath = Path.Combine(SettingsDirectory, "web-link-templates.json");
		WebMonitorRulesPath = Path.Combine(SettingsDirectory, "web-monitor-rules.json");
		ScenariosPath = Path.Combine(SettingsDirectory, "scenarios.json");
		GitHelpersPath = Path.Combine(SettingsDirectory, "git-helpers.json");
		RecentDirectoriesPath = Path.Combine(SettingsDirectory, "recent-directories.json");
		WindowLayoutPath = Path.Combine(SettingsDirectory, "window-layout.json");
		AppearancePath = Path.Combine(SettingsDirectory, "appearance.json");
		NotesDirectory = Path.Combine(SettingsDirectory, "Notes");

	}

	/// <summary>Gets the single Pact data root.</summary>
	public string RootDirectory { get; }

	/// <summary>Gets the durable user and application settings directory.</summary>
	public string SettingsDirectory { get; }

	/// <summary>Gets the shared WebView2 user-data directory.</summary>
	public string WebViewDirectory { get; }

	/// <summary>Gets the directory containing disposable, bounded application logs.</summary>
	public string LogsDirectory { get; }

	/// <summary>Gets the directory whose contents may always be removed.</summary>
	public string TempDirectory { get; }

	/// <summary>Gets the disposable Temp subtree that is cleared during application startup and shutdown.</summary>
	public string SessionTempDirectory { get; }

	/// <summary>Gets the Temp subtree that survives application restarts until its owning feature removes data.</summary>
	public string RetainedTempDirectory { get; }

	/// <summary>Gets the retained per-web-page monitoring snapshot directory.</summary>
	public string WebMonitorSnapshotsDirectory { get; }

	/// <summary>
	/// Gets the directory holding the generated agent-control configuration. It is retained rather
	/// than staged per session: the document carries no credential and is identical for every
	/// session, and clearing it while sessions are alive would break the next agent that reads it.
	/// </summary>
	public string AgentControlDirectory { get; }

	/// <summary>Gets the retained directory containing Pact-owned agent guidance.</summary>
	public string PactSkillsDirectory { get; }

	/// <summary>Gets the published Pact MCP miniskill path.</summary>
	public string PactMcpSkillPath { get; }

	/// <summary>Gets the published common Pact miniskill path.</summary>
	public string PactCommonSkillPath { get; }

	/// <summary>Gets the staging directory for atomic writes to Pact settings.</summary>
	public string AtomicTempDirectory { get; }

	/// <summary>Gets the durable project and saved-session state path.</summary>
	public string ProjectsPath { get; }

	/// <summary>Gets the durable project-independent terminal and browser state path.</summary>
	public string RootTabsPath { get; }

	/// <summary>Gets the editable shell profile settings path.</summary>
	public string ShellProfilesPath { get; }

	/// <summary>Gets the reviewer-only launch profile settings path.</summary>
	public string ReviewProfilesPath { get; }

	/// <summary>Gets the configured loopback agent-control endpoint settings path.</summary>
	public string AgentControlSettingsPath { get; }

	/// <summary>Gets the dedicated orchestrator slot settings path.</summary>
	public string OrchestratorPath { get; }

	/// <summary>Gets the editable prompt template settings path.</summary>
	public string PromptTemplatesPath { get; }

	/// <summary>Gets the editable web link template settings path.</summary>
	public string WebLinkTemplatesPath { get; }

	/// <summary>Gets the editable declarative web-monitoring rules path.</summary>
	public string WebMonitorRulesPath { get; }

	/// <summary>Gets the editable scenario definition settings path.</summary>
	public string ScenariosPath { get; }

	/// <summary>Gets the editable Git helper settings path.</summary>
	public string GitHelpersPath { get; }

	/// <summary>Gets the recent directory history settings path.</summary>
	public string RecentDirectoriesPath { get; }

	/// <summary>Gets the persisted window layout path.</summary>
	public string WindowLayoutPath { get; }

	/// <summary>Gets the persisted application appearance preference path.</summary>
	public string AppearancePath { get; }

	/// <summary>Gets the directory containing durable project note files.</summary>
	public string NotesDirectory { get; }

}

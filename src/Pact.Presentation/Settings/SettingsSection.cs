namespace Pact.Presentation.Settings;

/// <summary>
/// The sections of the settings window, in navigation order. Each maps to one editable settings
/// file or to an in-app preference.
/// </summary>
public enum SettingsSection
{
	/// <summary>Color theme preference.</summary>
	Appearance,

	/// <summary>Project-independent ROOT terminal and browser tabs.</summary>
	RootTabs,

	/// <summary>Active projects and their per-project settings.</summary>
	Projects,

	/// <summary>Parked projects, which can be restored or removed.</summary>
	PausedProjects,

	/// <summary>Agent and shell launch profiles.</summary>
	LaunchProfiles,

	/// <summary>Reviewer-only launch profiles used by agent-requested reviews.</summary>
	ReviewProfiles,

	/// <summary>Dedicated Hermes orchestrator slot and its lock-state routine.</summary>
	Orchestrator,

	/// <summary>Reusable prompt and terminal-command templates.</summary>
	PromptTemplates,

	/// <summary>Per-project web link templates.</summary>
	WebLinkTemplates,

	/// <summary>Declarative URL and DOM extractor rules for loaded web tabs.</summary>
	WebMonitoringRules,

	/// <summary>Review-loop scenario definitions.</summary>
	Scenarios,

	/// <summary>External git tools and the git panel's button commands.</summary>
	GitHelpers,

	/// <summary>Recently used project directories.</summary>
	RecentFolders
}

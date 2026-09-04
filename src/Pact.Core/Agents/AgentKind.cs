namespace Pact.Core.Agents;

/// <summary>
/// Identifies which coding agent a launch profile starts, selecting the terminal
/// compatibility behavior that agent requires. Terminal features differ per agent
/// (mouse tracking, newline encoding, resume-id extraction), so this drives input
/// rewriting and screen-verdict profile selection rather than being cosmetic.
/// </summary>
public enum AgentKind
{
	/// <summary>OpenAI Codex CLI. Enables mouse input through Win32 console mode
	/// rather than VT sequences, and needs win32-input-mode newline rewriting.</summary>
	Codex,

	/// <summary>Claude Code CLI. Emits VT mouse tracking itself and owns its
	/// selection, so text leaves it through the clipboard (OSC 52).</summary>
	Claude,

	/// <summary>Hermes agent CLI.</summary>
	Hermes,

	/// <summary>PowerShell. A plain shell with no mouse tracking, so xterm keeps
	/// native scrollback and selection.</summary>
	Pwsh,

	/// <summary>A user-defined profile whose behavior is not known in advance;
	/// treated with the conservative default terminal handling.</summary>
	Custom
}
using Pact.Core.Agents;

namespace Pact.Core.Prompting;

/// <summary>
/// Decides how a prompt template may be delivered and which sessions can receive it.
/// Centralized so the settings UI, quick actions, and selection actions all agree.
/// </summary>
public static class PromptActionPolicy
{
	/// <summary>
	/// Resolves a stored action type to the one behavior actually implemented, mapping both
	/// <see langword="null"/> (written before the field existed) and the legacy
	/// <see cref="PromptActionType.SelectionTemplate"/> to <see cref="PromptActionType.Prompt"/>.
	/// </summary>
	public static PromptActionType Normalize(PromptActionType? type) =>
		type == PromptActionType.TerminalCommand
			? PromptActionType.TerminalCommand
			: PromptActionType.Prompt;

	/// <summary>
	/// Whether a template of this type can be delivered to a session running
	/// <paramref name="kind"/>. Prompts require an agent with a composer, while terminal
	/// commands require a plain shell, so the two sets are deliberately disjoint.
	/// </summary>
	public static bool CanTarget(PromptActionType type, AgentKind kind) =>
		Normalize(type) switch
		{
			PromptActionType.Prompt => kind is AgentKind.Codex or AgentKind.Claude or AgentKind.Hermes,
			PromptActionType.TerminalCommand => kind is AgentKind.Pwsh or AgentKind.Custom,
			_ => false
		};

	/// <summary>
	/// Whether delivering <paramref name="template"/> should also submit it. A missing
	/// template never submits.
	/// </summary>
	public static bool ShouldSubmit(PromptTemplateRecord? template) =>
		template?.SendByDefault == true;
}
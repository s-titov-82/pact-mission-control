using Pact.Core.Agents;
using Pact.Core.Prompting;

namespace Pact.Core.Tests.Prompting;

public sealed class PromptActionPolicyTests
{
	[Test]
	[TestCase(PromptActionType.Prompt, AgentKind.Codex, true)]
	[TestCase(PromptActionType.Prompt, AgentKind.Claude, true)]
	[TestCase(PromptActionType.Prompt, AgentKind.Hermes, true)]
	[TestCase(PromptActionType.Prompt, AgentKind.Pwsh, false)]
	[TestCase(PromptActionType.Prompt, AgentKind.Custom, false)]
	[TestCase(PromptActionType.TerminalCommand, AgentKind.Codex, false)]
	[TestCase(PromptActionType.TerminalCommand, AgentKind.Pwsh, true)]
	[TestCase(PromptActionType.TerminalCommand, AgentKind.Custom, true)]
	public void CanTarget_matches_action_and_session_kind(
		PromptActionType type,
		AgentKind kind,
		bool expected) => PromptActionPolicy.CanTarget(type, kind).ShouldBe(expected);

	[Test]
	[TestCase(PromptActionType.Prompt, false, false)]
	[TestCase(PromptActionType.Prompt, true, true)]
	[TestCase(PromptActionType.TerminalCommand, false, false)]
	[TestCase(PromptActionType.TerminalCommand, true, true)]
	public void ShouldSubmit_reads_the_template_flag_not_its_type(
		PromptActionType type,
		bool sendByDefault,
		bool expected)
	{
		PromptTemplateRecord template = new(
			"template",
			"Template",
			"body",
			sendByDefault,
			type);

		PromptActionPolicy.ShouldSubmit(template).ShouldBe(expected);
	}

	[Test]
	public void ShouldSubmit_returns_false_for_raw_selection() => PromptActionPolicy.ShouldSubmit(template: null).ShouldBeFalse();
}
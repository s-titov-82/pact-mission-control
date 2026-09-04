using Pact.Core.Prompting;

namespace Pact.Core.Tests.Prompting;

public sealed class PromptTemplateRecordTests
{
	[Test]
	public void EffectiveType_uses_explicit_type()
	{
		PromptTemplateRecord template = new(
			"git-status",
			"git status",
			"git status",
			SendByDefault: false,
			PromptActionType.TerminalCommand);

		template.EffectiveType.ShouldBe(PromptActionType.TerminalCommand);
	}

	[Test]
	[TestCase(null)]
	[TestCase(PromptActionType.Prompt)]
	[TestCase(PromptActionType.SelectionTemplate)]
	public void EffectiveType_normalizes_legacy_prompt_forms_to_prompt(PromptActionType? storedType)
	{
		PromptTemplateRecord template = new(
			"legacy",
			"Legacy",
			"Review {selectedText}",
			SendByDefault: false,
			storedType);

		template.EffectiveType.ShouldBe(PromptActionType.Prompt);
	}

	[Test]
	[TestCase("Review {selectedText}", true)]
	[TestCase("Review {SELECTEDTEXT}", false)]
	[TestCase("Static body", false)]
	[TestCase(null, false)]
	public void UsesSelectedText_is_derived_from_the_exact_body_token(string? body, bool expected)
	{
		PromptTemplateRecord template = new(
			"template",
			"Template",
			body!,
			SendByDefault: false,
			PromptActionType.TerminalCommand);

		template.UsesSelectedText.ShouldBe(expected);
	}
}
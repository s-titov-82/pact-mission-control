using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class PactInstructionComposerTests
{
	[TestCase(AgentKind.Pwsh)]
	[TestCase(AgentKind.Custom)]
	[TestCase(AgentKind.Hermes)]
	public void BuildArguments_returns_empty_for_unsupported_kinds(AgentKind kind)
	{
		PactInstructionComposer.BuildArguments(
			kind,
			agentControlEnabled: true,
			Publication()).ShouldBeEmpty();
	}

	[Test]
	public void Codex_returns_a_raw_config_key_value_without_nested_toml_quotes()
	{
		IReadOnlyList<string> arguments = PactInstructionComposer.BuildArguments(
			AgentKind.Codex,
			agentControlEnabled: true,
			Publication());

		arguments[0].ShouldBe("-c");
		arguments[1].ShouldStartWith("developer_instructions=");
		arguments[1].ShouldContain("PactMcpSkill.md");
		arguments[1].ShouldContain("PactCommonSkill.md");
		arguments[1].ShouldNotContain("developer_instructions=\"");
	}

	[Test]
	public void Codex_common_only_instruction_does_not_claim_mcp_is_available()
	{
		IReadOnlyList<string> arguments = PactInstructionComposer.BuildArguments(
			AgentKind.Codex,
			agentControlEnabled: false,
			Publication());

		arguments[1].ShouldContain("PactCommonSkill.md");
		arguments[1].ShouldNotContain("PactMcpSkill.md");
		arguments[1].ShouldNotContain("MCP server is available");
	}

	[Test]
	public void Claude_returns_the_shared_short_instruction_as_one_raw_value()
	{
		IReadOnlyList<string> arguments = PactInstructionComposer.BuildArguments(
			AgentKind.Claude,
			agentControlEnabled: true,
			Publication());

		arguments[0].ShouldBe("--append-system-prompt");
		arguments[1].ShouldContain("PactMcpSkill.md");
		arguments[1].ShouldContain("PactCommonSkill.md");
		arguments[1].ShouldContain("Before the first use");
	}

	[Test]
	public void BuildArguments_preserves_special_path_characters_in_raw_values()
	{
		const string mcpPath = """C:\Pact Root\$cash`tick's "quoted"\PactMcpSkill.md""";
		const string commonPath = """C:\Pact Root\$cash`tick's "quoted"\PactCommonSkill.md\""";

		IReadOnlyList<string> arguments = PactInstructionComposer.BuildArguments(
			AgentKind.Claude,
			agentControlEnabled: true,
			new PactSkillPublication(mcpPath, commonPath));

		arguments[1].ShouldContain(mcpPath);
		arguments[1].ShouldContain(commonPath);
	}

	[Test]
	public void Missing_required_publication_paths_returns_empty()
	{
		PactInstructionComposer.BuildArguments(
			AgentKind.Codex,
			agentControlEnabled: true,
			PactSkillPublication.Empty).ShouldBeEmpty();
	}

	private static PactSkillPublication Publication() =>
		new(
			@"C:\Pact\Temp\Retained\PactSkills\PactMcpSkill.md",
			@"C:\Pact\Temp\Retained\PactSkills\PactCommonSkill.md");
}

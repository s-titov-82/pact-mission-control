using Pact.Presentation.Settings;

namespace Pact.Presentation.Tests.Settings;

public sealed class SettingsHelpContentTests
{
	public static IEnumerable<object[]> AllSections()
		=> Enum.GetValues<SettingsSection>().Select(section => new object[] { section });

	[Test]
	[TestCaseSource(nameof(AllSections))]
	public void Every_section_has_a_non_empty_title_and_body(SettingsSection section)
	{
		(var title, var body) = SettingsHelpContent.Get(section);

		string.IsNullOrWhiteSpace(title).ShouldBeFalse();
		string.IsNullOrWhiteSpace(body).ShouldBeFalse();
	}

	[Test]
	public void Unknown_section_value_throws() => Should.Throw<ArgumentOutOfRangeException>(
			() => SettingsHelpContent.Get((SettingsSection)999));

	[Test]
	public void Orchestrator_has_specific_help_text()
	{
		(var title, var body) = SettingsHelpContent.Get(SettingsSection.Orchestrator);

		title.ShouldBe("Orchestrator");
		body.ShouldContain("Initialize");
		body.ShouldContain("credential");
	}

	[TestCase(SettingsSection.Scenarios, "footer-complete reviewer response file")]
	[TestCase(SettingsSection.Scenarios, "Manual Pause")]
	[TestCase(SettingsSection.Appearance, "external process metrics")]
	[TestCase(SettingsSection.WebLinkTemplates, "project and ROOT")]
	public void Help_covers_current_runtime_behavior(SettingsSection section, string expectedText)
	{
		var (_, body) = SettingsHelpContent.Get(section);

		body.ShouldContain(expectedText);
	}
}

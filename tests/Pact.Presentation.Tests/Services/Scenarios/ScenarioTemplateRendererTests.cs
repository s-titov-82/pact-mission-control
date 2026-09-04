using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Tests.Services.Scenarios;

public sealed class ScenarioTemplateRendererTests
{
	[Test]
	public void Render_ReplacesSupportedPlaceholders()
	{
		var rendered = ScenarioTemplateRenderer.Render(
			"Subject={subject}; Feedback={reviewerOutput}; Tail={reviewerInstruction}; Marker={stopMarkerPrefix}+{stopMarkerSuffix}",
			new Dictionary<string, string>
			{
				["subject"] = "diff",
				["reviewerOutput"] = "fix this",
				["reviewerInstruction"] = "strict tail",
				["stopMarkerPrefix"] = "AGENT_TERMINAL_",
				["stopMarkerSuffix"] = "DONE"
			});

		rendered.ShouldBe("Subject=diff; Feedback=fix this; Tail=strict tail; Marker=AGENT_TERMINAL_+DONE");
	}

	[Test]
	public void Render_LeavesMissingPlaceholdersVisible()
	{
		var rendered = ScenarioTemplateRenderer.Render(
			"Subject={subject}; Feedback={reviewerOutput}",
			new Dictionary<string, string> { ["subject"] = "plan" });

		rendered.ShouldBe("Subject=plan; Feedback={reviewerOutput}");
	}

	[Test]
	public void Render_RejectsNullTemplate() => Should.Throw<ArgumentNullException>(() =>
														 ScenarioTemplateRenderer.Render(null!, new Dictionary<string, string>()));
}
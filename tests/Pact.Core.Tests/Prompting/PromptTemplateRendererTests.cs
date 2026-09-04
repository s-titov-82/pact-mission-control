using Pact.Core.Prompting;

namespace Pact.Core.Tests.Prompting;

public sealed class PromptTemplateRendererTests
{
	[Test]
	public void Render_replaces_known_variables()
	{
		PromptTemplateRenderer renderer = new();

		var result = renderer.Render(
			"Project: {project}\nTask: {task}\nText: {selectedText}",
			new Dictionary<string, string>
			{
				["project"] = "Pact",
				["task"] = "MVP",
				["selectedText"] = "review me"
			});

		result.ShouldBe("Project: Pact\nTask: MVP\nText: review me");
	}

	[Test]
	public void Render_leaves_unknown_variables_visible()
	{
		PromptTemplateRenderer renderer = new();

		var result = renderer.Render("Value: {missing}", new Dictionary<string, string>());

		result.ShouldBe("Value: {missing}");
	}
}
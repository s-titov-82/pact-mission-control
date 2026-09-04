using Pact.Presentation.Services;
using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Tests.Services;

public sealed class ScenarioCatalogTests
{
	[Test]
	public void TryGet_returns_review_loop_blueprint_for_review_loop_kind()
	{
		var found = ScenarioCatalog.TryGet(ScenarioKind.ReviewLoop, out var blueprint);

		found.ShouldBeTrue();
		blueprint.ShouldBeSameAs(ReviewLoopScenarioProgram.Blueprint);
	}
}
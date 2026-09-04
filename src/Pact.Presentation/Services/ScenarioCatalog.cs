using Pact.Core.Scenarios;
using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Services;

/// <summary>
/// Maps a scenario kind to the blueprint describing its roles and steps.
/// </summary>
public static class ScenarioCatalog
{
	/// <summary>
	/// Looks up the blueprint for <paramref name="kind"/>.
	/// </summary>
	/// <param name="kind">Scenario kind from the definition.</param>
	/// <param name="blueprint">The blueprint when the kind is implemented.</param>
	/// <returns>
	/// <see langword="false"/> for a kind this build does not implement, which lets a
	/// <c>scenarios.json</c> written by a newer version be skipped instead of crashing the
	/// scenarios list.
	/// </returns>
	public static bool TryGet(
		ScenarioKind kind,
		out ScenarioBlueprint blueprint)
	{
		switch (kind)
		{
			case ScenarioKind.ReviewLoop:
				blueprint = ReviewLoopScenarioProgram.Blueprint;
				return true;
			default:
				blueprint = null!;
				return false;
		}
	}
}
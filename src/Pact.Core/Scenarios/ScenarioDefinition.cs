namespace Pact.Core.Scenarios;

/// <summary>
/// Which scenario program a definition drives. There is no id allow-list: any
/// <c>scenarios.json</c> entry with a known kind is runnable.
/// </summary>
public enum ScenarioKind
{
	/// <summary>The fixed author/reviewer review loop.</summary>
	ReviewLoop
}

/// <summary>Settings for one named review. Pact supplies the turn protocol.</summary>
/// <param name="Id">Stable key named by an MCP review request.</param>
/// <param name="Kind">Scenario program to run.</param>
/// <param name="Name">Display label.</param>
/// <param name="ReviewRules">Checks supplied to the reviewer on its first turn.</param>
/// <param name="DefaultStrict">Whether approval requires resolving findings of every severity.</param>
public sealed record ScenarioDefinition(
	string Id,
	ScenarioKind Kind,
	string Name,
	string ReviewRules,
	bool DefaultStrict = true)
{
	/// <summary>An existing review type's round limit when the request supplies no override.</summary>
	public int? MaxIterations { get; init; }
}

namespace Pact.Core.Scenarios;

/// <summary>
/// What one blueprint step does, used to describe a scenario's shape in the journal.
/// </summary>
public enum ScenarioStepKind
{
	/// <summary>Publishes a task file and triggers the target agent.</summary>
	Send,

	/// <summary>Waits for and reads the agent's response file.</summary>
	Capture,

	/// <summary>Tests the captured response for the stop marker.</summary>
	Decision,

	/// <summary>Returns to an earlier step for the next iteration.</summary>
	LoopBack
}

/// <summary>
/// Describes one step of a scenario for display purposes. This is documentation of the
/// program's shape, not the executable definition.
/// </summary>
/// <param name="Id">Step key, unique within the blueprint.</param>
/// <param name="FromRole">Role the step acts on behalf of.</param>
/// <param name="ToRole">Role receiving the step's output, or <see langword="null"/> when it targets no role.</param>
/// <param name="Description">Human-readable summary shown in the journal.</param>
/// <param name="Kind">What the step does.</param>
public sealed record ScenarioStepMetadata(
	string Id,
	string FromRole,
	string? ToRole,
	string Description,
	ScenarioStepKind Kind);

/// <summary>
/// Declared shape of a scenario: its roles and the steps they exchange.
/// </summary>
/// <param name="ScenarioId">Definition this blueprint describes.</param>
/// <param name="Name">Display name.</param>
/// <param name="Roles">Role names that setup binds to live sessions.</param>
/// <param name="Steps">Steps in execution order.</param>
/// <param name="DefaultMaxIterations">Iteration budget suggested by the blueprint.</param>
/// <param name="DefaultTarget">Review target suggested by the blueprint.</param>
/// <param name="CompletionNoticeRole">
/// Role told when the run reaches a terminal state. This finalization policy is not a step;
/// <see langword="null"/> disables the notice.
/// </param>
public sealed record ScenarioBlueprint(
	string ScenarioId,
	string Name,
	string[] Roles,
	ScenarioStepMetadata[] Steps,
	int DefaultMaxIterations,
	string DefaultTarget,
	string? CompletionNoticeRole = null)
{
	/// <summary>
	/// Verifies the blueprint is internally consistent: step ids are unique, and every role a
	/// step references is declared in <see cref="Roles"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// A step id repeats, or a step references an undeclared role. Both would leave a run
	/// unable to resolve its target session, so they fail here rather than mid-run.
	/// </exception>
	public void Validate()
	{
		HashSet<string> roles = new(Roles, StringComparer.Ordinal);
		HashSet<string> stepIds = new(StringComparer.Ordinal);
		foreach (var step in Steps)
		{
			if (!stepIds.Add(step.Id))
			{
				throw new InvalidOperationException($"Duplicate step id '{step.Id}' in scenario '{ScenarioId}'.");
			}

			if (!roles.Contains(step.FromRole)
				|| (step.ToRole is not null && !roles.Contains(step.ToRole)))
			{
				throw new InvalidOperationException($"Step '{step.Id}' references undeclared role in scenario '{ScenarioId}'.");
			}
		}

		if (CompletionNoticeRole is not null && !roles.Contains(CompletionNoticeRole))
		{
			throw new InvalidOperationException(
				$"Completion notice role '{CompletionNoticeRole}' is undeclared in scenario '{ScenarioId}'.");
		}
	}
}

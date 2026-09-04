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

/// <summary>
/// A selectable preset for the reviewer's standing instructions. Reviewer discipline is
/// editable text rather than a fixed enum, so presets seed the field instead of constraining it.
/// </summary>
/// <param name="Id">Stable key referenced by <see cref="ScenarioDefinition.DefaultReviewerInstructionId"/>.</param>
/// <param name="Name">Label shown in the scenario setup dialog.</param>
/// <param name="Text">Instruction text, editable before the run starts.</param>
public sealed record ScenarioReviewerInstruction(
	string Id,
	string Name,
	string Text);

/// <summary>
/// One configured scenario from <c>scenarios.json</c>.
/// </summary>
/// <remarks>
/// The four templates drive a review loop over <paramref name="MaxIterations"/> passes:
/// <paramref name="StartPromptTemplate"/> briefs the reviewer for pass 1,
/// <paramref name="FirstFeedbackTemplate"/> carries those findings to the author,
/// <paramref name="AuthorReturnTemplate"/> returns the author's reply for re-verification in
/// passes 2..N, and <paramref name="FeedbackTemplate"/> carries follow-up findings back.
/// The engine appends its own protocol blocks to every rendered template, so the text sent to
/// an agent is never exactly the template body.
/// </remarks>
/// <param name="Id">Stable key; also used to persist <paramref name="DefaultTarget"/>.</param>
/// <param name="Kind">Scenario program to run.</param>
/// <param name="Name">Label shown in the scenarios list.</param>
/// <param name="MaxIterations">
/// Review pass budget. Exhausting it ends the run as
/// <see cref="ScenarioRunState.MaxIterationsReached"/> rather than as a success.
/// </param>
/// <param name="StopMarker">
/// Exact text the reviewer emits to declare completion. This machine-checked marker is the
/// only accepted completion signal; terminal busy/idle state never ends a run.
/// </param>
/// <param name="DefaultTarget">Review scope pointer or text prefilled in the setup dialog.</param>
/// <param name="StartPromptTemplate">Pass 1 brief for the reviewer.</param>
/// <param name="FirstFeedbackTemplate">Pass 1 findings sent to the author.</param>
/// <param name="AuthorReturnTemplate">Author's reply returned to the reviewer in passes 2..N.</param>
/// <param name="FeedbackTemplate">Follow-up findings sent to the author in passes 2..N.</param>
/// <param name="ReviewerInstructions">Selectable reviewer instruction presets.</param>
/// <param name="DefaultReviewerInstructionId">Preset selected when the dialog opens.</param>
public sealed record ScenarioDefinition(
	string Id,
	ScenarioKind Kind,
	string Name,
	int MaxIterations,
	string StopMarker,
	string DefaultTarget,
	string StartPromptTemplate,
	string FirstFeedbackTemplate,
	string AuthorReturnTemplate,
	string FeedbackTemplate,
	ScenarioReviewerInstruction[] ReviewerInstructions,
	string DefaultReviewerInstructionId);
using Pact.Core.Scenarios;

namespace Pact.Core.Tests.Scenarios;

public sealed class ScenarioBlueprintTests
{
	private static ScenarioBlueprint CreateBlueprint(ScenarioStepMetadata[] steps) =>
		new("id", "Name", ["author", "reviewer"], steps, DefaultMaxIterations: 5, DefaultTarget: "start");

	[Test]
	public void Validate_AcceptsStepsReferencingDeclaredRoles()
	{
		var blueprint = CreateBlueprint(
			[new("s1", "author", "reviewer", "send diff", ScenarioStepKind.Send)]);

		blueprint.Validate();
	}

	[Test]
	public void Validate_ThrowsWhenStepReferencesUnknownRole()
	{
		var blueprint = CreateBlueprint(
			[new("s1", "author", "ghost", "send", ScenarioStepKind.Send)]);

		Should.Throw<InvalidOperationException>(blueprint.Validate);
	}

	[Test]
	public void Validate_ThrowsOnDuplicateStepIds()
	{
		var blueprint = CreateBlueprint(
		[
			new("s1", "author", "reviewer", "send", ScenarioStepKind.Send),
			new("s1", "reviewer", null, "capture", ScenarioStepKind.Capture)
		]);

		Should.Throw<InvalidOperationException>(blueprint.Validate);
	}

	[Test]
	public void Validate_ThrowsWhenCompletionNoticeRoleIsUnknown()
	{
		ScenarioBlueprint blueprint = new(
			"id",
			"Name",
			["author", "reviewer"],
			[new("s1", "author", "reviewer", "send", ScenarioStepKind.Send)],
			DefaultMaxIterations: 5,
			DefaultTarget: "start",
			CompletionNoticeRole: "ghost");

		Should.Throw<InvalidOperationException>(blueprint.Validate);
	}
}

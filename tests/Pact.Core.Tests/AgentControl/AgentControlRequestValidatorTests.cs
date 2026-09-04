using Pact.Core.AgentControl;

namespace Pact.Core.Tests.AgentControl;

public sealed class AgentControlRequestValidatorTests
{
	private static readonly AgentControlOwner ProjectOwner = new(IsRoot: false, ProjectId: "project-1");
	private static readonly AgentControlOwner RootOwner = new(IsRoot: true, ProjectId: null);

	[Test]
	public void Validate_AcceptsReviewFromProjectSession()
	{
		RequestReviewRequest request = new("plan-review", "claude-opus", "docs/plan.md", MaxIterations: null);

		AgentControlRequestValidator.Validate(ProjectOwner, request).ShouldBeNull();
	}

	[Test]
	public void Validate_RefusesReviewFromRootSession()
	{
		RequestReviewRequest request = new("plan-review", "claude-opus", "docs/plan.md", MaxIterations: null);

		AgentControlRequestValidator.Validate(RootOwner, request)!.Code.ShouldBe("owner-not-a-project");
	}

	[Test]
	public void Validate_RefusesReviewWithBlankTarget()
	{
		RequestReviewRequest request = new("plan-review", "claude-opus", "   ", MaxIterations: null);

		AgentControlRequestValidator.Validate(ProjectOwner, request)!.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void Validate_RefusesReviewWithNonPositiveMaxIterations()
	{
		RequestReviewRequest request = new("plan-review", "claude-opus", "docs/plan.md", MaxIterations: 0);

		AgentControlRequestValidator.Validate(ProjectOwner, request)!.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void Validate_RefusesNoteFromRootSession()
	{
		AgentControlRequestValidator.Validate(RootOwner, new AppendNoteRequest("text"))!
			.Code.ShouldBe("owner-not-a-project");
	}

	[Test]
	public void Validate_RefusesBlankNote()
	{
		AgentControlRequestValidator.Validate(ProjectOwner, new AppendNoteRequest("  "))!
			.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void Validate_project_notes_owner_refuses_root()
	{
		AgentControlRequestValidator.ValidateProjectNotesOwner(RootOwner)!
			.Code.ShouldBe("owner-not-a-project");
		AgentControlRequestValidator.ValidateProjectNotesOwner(ProjectOwner)
			.ShouldBeNull();
	}

	[Test]
	public void Replace_notes_allows_empty_text_but_requires_revision()
	{
		AgentControlRequestValidator.Validate(
			ProjectOwner,
			new ReplaceNoteRequest(string.Empty, "revision"))
			.ShouldBeNull();

		AgentControlRequestValidator.Validate(
			ProjectOwner,
			new ReplaceNoteRequest("text", " "))!
			.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void Replace_notes_refuses_a_root_owner()
	{
		AgentControlRequestValidator.Validate(
			RootOwner,
			new ReplaceNoteRequest("text", "revision"))!
			.Code.ShouldBe("owner-not-a-project");
	}

	[Test]
	public void Validate_AcceptsWebTabFromRootSession()
	{
		AgentControlRequestValidator.Validate(RootOwner, new OpenWebTabRequest("https://example.com", null))
			.ShouldBeNull();
	}

	[Test]
	public void Validate_RefusesNonHttpWebTab()
	{
		AgentControlRequestValidator.Validate(
			ProjectOwner,
			new OpenWebTabRequest("file:///c:/secret.txt", null))!
			.Code.ShouldBe("invalid-argument");
	}
}

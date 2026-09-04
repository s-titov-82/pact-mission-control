using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ScenarioSetupViewModelTests
{
	[Test]
	public void Constructor_prefills_role_bindings_with_distinct_sessions_by_order()
	{
		var first = CreateSession("first-session", "First");
		var second = CreateSession("second-session", "Second");

		var viewModel = CreateViewModel([first, second]);

		viewModel.RoleBindings.Single(binding => binding.Role == "author").SelectedSession.ShouldBeSameAs(first);
		viewModel.RoleBindings.Single(binding => binding.Role == "reviewer").SelectedSession.ShouldBeSameAs(second);
	}

	[Test]
	public void CanRun_is_false_when_a_role_is_unbound()
	{
		var author = CreateSession("author-session", "Author");

		var viewModel = CreateViewModel([author]);

		viewModel.CanRun.ShouldBeFalse();
		viewModel.ValidationMessage.ShouldNotBeNull();
	}

	[Test]
	public void CanRun_is_false_when_two_roles_select_the_same_session()
	{
		var author = CreateSession("author-session", "Author");
		var reviewer = CreateSession("reviewer-session", "Reviewer");
		var viewModel = CreateViewModel([author, reviewer]);

		viewModel.RoleBindings.Single(binding => binding.Role == "reviewer").SelectedSession = author;

		viewModel.CanRun.ShouldBeFalse();
		viewModel.ValidationMessage.ShouldBe("Each role must use a distinct running session.");
	}

	[Test]
	public void CanRun_is_false_when_selected_session_is_locked()
	{
		var author = CreateSession("author-session", "Author");
		var reviewer = CreateSession("reviewer-session", "Reviewer");
		reviewer.LockForScenario("run-1");

		var viewModel = CreateViewModel([author, reviewer]);

		viewModel.CanRun.ShouldBeFalse();
		viewModel.ValidationMessage.ShouldBe("Selected sessions must not already be locked by a scenario.");
	}

	[Test]
	public void CanRun_is_false_when_selected_session_is_not_running()
	{
		var author = CreateSession("author-session", "Author");
		var reviewer = CreateSession("reviewer-session", "Reviewer", SessionStatus.Stopped);

		var viewModel = CreateViewModel([author, reviewer]);

		viewModel.CanRun.ShouldBeFalse();
		viewModel.ValidationMessage.ShouldBe("Selected sessions must be running.");
	}

	[Test]
	public void CanRun_is_true_when_all_roles_have_distinct_running_unlocked_sessions()
	{
		var author = CreateSession("author-session", "Author");
		var reviewer = CreateSession("reviewer-session", "Reviewer");

		var viewModel = CreateViewModel([author, reviewer]);

		viewModel.CanRun.ShouldBeTrue();
		viewModel.ValidationMessage.ShouldBeNull();
	}

	[Test]
	public void Constructor_uses_blueprint_default_target_when_definition_target_is_empty()
	{
		var definition = CreateDefinition() with
		{
			DefaultTarget = string.Empty
		};

		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), definition, []);

		viewModel.Target.ShouldBe("default target");
	}

	[Test]
	public void Constructor_uses_definition_default_target_when_present()
	{
		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), CreateDefinition(), []);

		viewModel.Target.ShouldBe("definition target");
	}

	[Test]
	public void SaveTargetAsDefault_defaults_to_false_and_is_settable()
	{
		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), CreateDefinition(), []);

		viewModel.SaveTargetAsDefault.ShouldBeFalse();

		viewModel.SaveTargetAsDefault = true;

		viewModel.SaveTargetAsDefault.ShouldBeTrue();
	}

	[Test]
	public void Constructor_uses_default_reviewer_instruction_and_exposes_options()
	{
		var definition = CreateDefinition() with
		{
			DefaultReviewerInstructionId = "blocking-only"
		};

		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), definition, []);

		viewModel.SelectedReviewerInstruction!.Name.ShouldBe("Blocking issues only");
		viewModel.ReviewerInstructionText.ShouldBe("Blocker tail");
		viewModel.ReviewerInstructionOptions.Select(option => option.Id).ToArray().ShouldBe(["critical-issues-only", "blocking-only", "strict"]);
	}

	[Test]
	public void Setting_selected_reviewer_instruction_updates_editable_text()
	{
		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), CreateDefinition(), []);

		viewModel.SelectedReviewerInstruction = viewModel.ReviewerInstructionOptions.Single(option => option.Id == "critical-issues-only");

		viewModel.ReviewerInstructionText.ShouldBe("Critical tail");
	}

	[Test]
	public void ReviewerInstructionText_can_be_edited_for_current_run()
	{
		ScenarioSetupViewModel viewModel = new(CreateBlueprint(), CreateDefinition(), [])
		{
			ReviewerInstructionText = "Custom run tail"
		};

		viewModel.ReviewerInstructionText.ShouldBe("Custom run tail");
	}

	[Test]
	public void BuildRoleBindings_returns_selected_session_ids_by_role()
	{
		var author = CreateSession("author-session", "Author");
		var reviewer = CreateSession("reviewer-session", "Reviewer");
		var viewModel = CreateViewModel([author, reviewer]);

		var bindings = viewModel.BuildRoleBindings();

		bindings["author"].ShouldBe("author-session");
		bindings["reviewer"].ShouldBe("reviewer-session");
	}

	private static ScenarioSetupViewModel CreateViewModel(IReadOnlyList<SessionViewModel> sessions) => new ScenarioSetupViewModel(CreateBlueprint(), CreateDefinition(), sessions);

	private static ScenarioDefinition CreateDefinition() => new ScenarioDefinition(
			"code-review",
			ScenarioKind.ReviewLoop,
			"Code review",
			MaxIterations: 3,
			StopMarker: "DONE",
			DefaultTarget: "definition target",
			StartPromptTemplate: "review {target} {reviewerInstruction} {stopMarkerPrefix} {stopMarkerSuffix}",
			FirstFeedbackTemplate: "feedback {target} {reviewerOutput}",
			AuthorReturnTemplate: "author return {authorOutput}",
			FeedbackTemplate: "feedback {reviewerOutput}",
			ReviewerInstructions:
			[
				new("critical-issues-only", "Critical issues only", "Critical tail"),
				new("blocking-only", "Blocking issues only", "Blocker tail"),
				new("strict", "Strict review", "Strict tail")
			],
			DefaultReviewerInstructionId: "strict");

	private static ScenarioBlueprint CreateBlueprint() => new ScenarioBlueprint(
			"code-review",
			"Code review",
			["author", "reviewer"],
			[
				new("send-review", "author", "reviewer", "Send review request", ScenarioStepKind.Send),
				new("capture-review", "reviewer", null, "Capture review", ScenarioStepKind.Capture)
			],
			DefaultMaxIterations: 5,
			DefaultTarget: "default target");

	private static SessionViewModel CreateSession(
		string id,
		string title,
		SessionStatus status = SessionStatus.Running) => new SessionViewModel(new SessionRecord(
			id,
			AgentKind.Codex,
			title,
			@"D:\repo",
			"codex",
			null,
			status,
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow));
}
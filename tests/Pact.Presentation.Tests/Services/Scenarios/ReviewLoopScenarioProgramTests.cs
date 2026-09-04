using Pact.Presentation.Services;
using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Tests.Services.Scenarios;

public sealed partial class ReviewLoopScenarioProgramTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _directory => _temporaryDirectory.Path;

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	[Test]
	public void Blueprint_is_valid() => ReviewLoopScenarioProgram.Blueprint.Validate();

	[Test]
	public async Task RunIterationAsync_pass1_publishes_task_with_target_instruction_and_completion_footer()
	{
		var definition = CreateDefinition(stopMarker: "DONE");
		FakeScenarioTerminalGateway gateway = new();
		TaskCompletionSource releaseFirstTask = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> firstTrigger = new(TaskCreationOptions.RunContinuationsAsynchronously);
		Queue<string> responses = new(["Finding 1", "Fix 1"]);
		gateway.PromptSentAsync = async (_, trigger, cancellationToken) =>
		{
			firstTrigger.TrySetResult(trigger);
			await releaseFirstTask.Task.WaitAsync(cancellationToken);
			await CompleteSentTaskAsync(trigger, responses.Dequeue(), cancellationToken);
		};
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 1);

		await firstTrigger.Task.WaitAsync(TimeSpan.FromSeconds(5));

		gateway.Sent[0].SessionId.ShouldBe("session-reviewer");
		gateway.Sent[0].Prompt.ShouldStartWith("Read and follow the complete instructions in \"");
		gateway.Sent[0].Prompt.Contains("Initial target", StringComparison.Ordinal).ShouldBeFalse();
		var firstTaskPath = ExtractTaskPath(gateway.Sent[0].Prompt);
		var firstTask = await File.ReadAllTextAsync(firstTaskPath);
		firstTask.Contains("Initial target", StringComparison.Ordinal).ShouldBeTrue();
		firstTask.Contains("strict tail", StringComparison.Ordinal).ShouldBeTrue();
		firstTask.Contains("Completion footer:", StringComparison.Ordinal).ShouldBeTrue();

		releaseFirstTask.TrySetResult();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Test]
	public async Task RunIterationAsync_removes_exchange_directory_after_completion()
	{
		var definition = CreateDefinition(stopMarker: "DONE");
		FakeScenarioTerminalGateway gateway = new();
		CompleteQueuedSentTasks(gateway, "DONE");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		var exchangeRoot = Path.Combine(_directory, ReviewLoopScenarioProgram.ExchangeRootDirectoryName);
		Directory.Exists(exchangeRoot).ShouldBeFalse();
	}

	[Test]
	public async Task RunIterationAsync_stops_when_reviewer_response_contains_marker_and_sends_only_completion_to_author()
	{
		var definition = CreateDefinition(stopMarker: "CUSTOM_DONE");
		FakeScenarioTerminalGateway gateway = new();
		CompleteQueuedSentTasks(gateway, "Looks ready.\nCUSTOM_DONE");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 3);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent.Count.ShouldBe(2);
		gateway.Sent[0].SessionId.ShouldBe("session-reviewer");
		gateway.Sent[1].SessionId.ShouldBe("session-author");
		gateway.Sent[1].Prompt.ShouldContain("Review loop finished");
		handle.Journal.ShouldContain(entry => entry.StepId == "check-consensus" && entry.Level == ScenarioJournalLevel.Success);
		handle.FinalResult.ShouldBe("Looks ready.\nCUSTOM_DONE");
		handle.FinalResult!.Contains("PACT_RESPONSE_COMPLETE", StringComparison.Ordinal).ShouldBeFalse();
		handle.FinalResult.Contains("Response file:", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Test]
	public async Task RunIterationAsync_pass1_no_marker_renders_first_feedback_into_author_task()
	{
		var definition = CreateDefinition(stopMarker: "DONE");
		FakeScenarioTerminalGateway gateway = new();
		var tasks = CompleteQueuedSentTasks(gateway, "Finding 1", "Fix 1", "DONE");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 5);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		gateway.Sent[1].SessionId.ShouldBe("session-author");
		tasks[1].Contains("FirstFeedback: target=Initial target reviewerOutput=Finding 1", StringComparison.Ordinal).ShouldBeTrue();
		tasks[1].Contains("Response file:", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task RunIterationAsync_pass2_renders_author_return_into_reviewer_task()
	{
		var definition = CreateDefinition(stopMarker: "DONE");
		FakeScenarioTerminalGateway gateway = new();
		var tasks = CompleteQueuedSentTasks(gateway, "Finding 1", "Fix 1", "DONE");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 5);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent[2].SessionId.ShouldBe("session-reviewer");
		tasks[2].Contains("AuthorReturn: authorOutput=Fix 1", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task RunIterationAsync_uses_distinct_task_and_response_paths_for_reviewer_author_and_pass_two()
	{
		var definition = CreateDefinition(stopMarker: "DONE");
		FakeScenarioTerminalGateway gateway = new();
		var tasks = CompleteQueuedSentTasks(gateway, "Finding 1", "Fix 1", "Finding 2", "Fix 2");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 2);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.MaxIterationsReached);
		gateway.Sent[3].SessionId.ShouldBe("session-author");
		tasks[3].Contains("Feedback: reviewerOutput=Finding 2", StringComparison.Ordinal).ShouldBeTrue();
		var taskPaths = gateway.Sent
			.Where(sent => sent.Prompt.StartsWith(
				"Read and follow the complete instructions in ",
				StringComparison.Ordinal))
			.Select(sent => ExtractTaskPath(sent.Prompt))
			.ToArray();
		var responsePaths = tasks
			.Select(task => ExtractProtocolValue(task, "Response file"))
			.ToArray();
		string[] observedTaskAndResponsePaths = [.. taskPaths, .. responsePaths];
		taskPaths.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
		responsePaths.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
		observedTaskAndResponsePaths.ShouldAllBe(path =>
			MyRegex().IsMatch(Path.GetFileName(path)));
		handle.Journal.ShouldNotContain(entry => entry.StepId == "check-consensus" && entry.Level == ScenarioJournalLevel.Success);
		handle.Journal.ShouldNotContain(entry =>
			entry.Message.Contains("busy for", StringComparison.OrdinalIgnoreCase));
		handle.FinalResult!.Contains("PACT_RESPONSE_COMPLETE", StringComparison.Ordinal).ShouldBeFalse();
		handle.FinalResult.Contains("Response file:", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Test]
	public async Task RunIterationAsync_appends_engine_completion_policy_when_template_has_no_policy_placeholders()
	{
		var definition = CreateDefinition(stopMarker: "DONE") with
		{
			StartPromptTemplate = "Brief: target={target}"
		};
		FakeScenarioTerminalGateway gateway = new();
		var tasks = CompleteQueuedSentTasks(gateway, "DONE");
		var handle = StartScenario(gateway, definition, "Initial target", maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		var task = tasks.Single();
		task.Contains("Completion marker policy:", StringComparison.Ordinal).ShouldBeTrue();
		task.Contains("strict tail", StringComparison.Ordinal).ShouldBeTrue();
		task.Contains("`DO`", StringComparison.Ordinal).ShouldBeTrue();
		task.Contains("`NE`", StringComparison.Ordinal).ShouldBeTrue();
		task.Contains("DONE", StringComparison.Ordinal).ShouldBeFalse();
	}

	private static string ExtractTaskPath(string trigger)
	{
		const string prefix = "Read and follow the complete instructions in \"";
		trigger.StartsWith(prefix, StringComparison.Ordinal).ShouldBeTrue();
		trigger.EndsWith("\".", StringComparison.Ordinal).ShouldBeTrue();
		return trigger[prefix.Length..^2];
	}

	private static string ExtractProtocolValue(string task, string label)
	{
		var prefix = $"{label}: `";
		var line = task.Split('\n').Single(candidate =>
			candidate.StartsWith(prefix, StringComparison.Ordinal));
		line.EndsWith('`').ShouldBeTrue();
		return line[prefix.Length..^1];
	}

	private static async Task CompleteSentTaskAsync(
		string trigger,
		string queuedContent,
		CancellationToken cancellationToken)
	{
		var taskPath = ExtractTaskPath(trigger);
		var task = await File.ReadAllTextAsync(taskPath, cancellationToken);
		var responsePath = ExtractProtocolValue(task, "Response file");
		var completionFooter = ExtractProtocolValue(task, "Completion footer");
		await File.WriteAllTextAsync(
			responsePath,
			$"{queuedContent}\n{completionFooter}\n",
			cancellationToken);
	}

	private static List<string> CompleteQueuedSentTasks(
		FakeScenarioTerminalGateway gateway,
		params string[] queuedContents)
	{
		Queue<string> responses = new(queuedContents);
		List<string> tasks = [];
		gateway.PromptSentAsync = async (_, trigger, cancellationToken) =>
		{
			var taskPath = ExtractTaskPath(trigger);
			tasks.Add(await File.ReadAllTextAsync(taskPath, cancellationToken));
			await CompleteSentTaskAsync(trigger, responses.Dequeue(), cancellationToken);
		};
		return tasks;
	}

	private ScenarioRunHandle StartScenario(
		FakeScenarioTerminalGateway gateway,
		ScenarioDefinition definition,
		string target,
		int maxIterations)
	{
		ScenarioRunService service = new(gateway);
		return service.Start(
			ReviewLoopScenarioProgram.Blueprint,
			new ReviewLoopScenarioProgram(
				definition,
				"strict tail",
				_directory,
				filePollInterval: TimeSpan.FromMilliseconds(10)),
			"project-1",
			new Dictionary<string, string>
			{
				["author"] = "session-author",
				["reviewer"] = "session-reviewer"
			},
			target,
			maxIterations);
	}

	private static ScenarioDefinition CreateDefinition(string stopMarker) => new ScenarioDefinition(
			"custom-review",
			ScenarioKind.ReviewLoop,
			"Custom review",
			MaxIterations: 3,
			StopMarker: stopMarker,
			DefaultTarget: "default target",
			StartPromptTemplate: "Brief: target={target} instruction={reviewerInstruction} markerParts={stopMarkerPrefix}+{stopMarkerSuffix}",
			FirstFeedbackTemplate: "FirstFeedback: target={target} reviewerOutput={reviewerOutput}",
			AuthorReturnTemplate: "AuthorReturn: authorOutput={authorOutput}",
			FeedbackTemplate: "Feedback: reviewerOutput={reviewerOutput}",
			ReviewerInstructions:
			[
				new ScenarioReviewerInstruction("strict", "Strict", "strict tail")
			],
			DefaultReviewerInstructionId: "strict");
	[System.Text.RegularExpressions.GeneratedRegex(@"pass-\d{3}-(reviewer|author)-(task|response)\.md$")]
	private static partial System.Text.RegularExpressions.Regex MyRegex();
}

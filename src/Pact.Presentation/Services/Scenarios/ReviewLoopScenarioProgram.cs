using Pact.Core.Scenarios;

namespace Pact.Presentation.Services.Scenarios;

/// <summary>
/// Fixed author/reviewer loop that publishes immutable per-step tasks and reads completed response
/// files. Terminal traffic is limited to a short task-path trigger and confirmation.
/// </summary>
public sealed class ReviewLoopScenarioProgram : IScenarioProgram, IScenarioRunArtifactCleaner
{
	/// <summary>Role name bound to the session that produces the work under review.</summary>
	public const string AuthorRole = "author";

	/// <summary>Role name bound to the session that reviews the work.</summary>
	public const string ReviewerRole = "reviewer";

	/// <summary>
	/// Directory under the project root holding each run's task and response files. It is
	/// Pact-owned: abandoned copies are removed at startup and each run deletes its own on exit.
	/// </summary>
	public const string ExchangeRootDirectoryName = ReviewExchangeDirectory.RootName;

	private const string ReviewerInstructionPlaceholder = "{reviewerInstruction}";
	private static readonly TimeSpan DefaultFilePollInterval = TimeSpan.FromMilliseconds(250);

	private readonly ScenarioDefinition _definition;
	private readonly string _reviewerInstruction;
	private readonly string _projectRootPath;
	private readonly TimeSpan _filePollInterval;

	/// <summary>Creates the fixed review loop with a configurable completed-response polling interval.</summary>
	public ReviewLoopScenarioProgram(
		ScenarioDefinition definition,
		string reviewerInstruction,
		string projectRootPath,
		TimeSpan? filePollInterval = null)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(reviewerInstruction);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

		_definition = definition;
		_reviewerInstruction = reviewerInstruction;
		_projectRootPath = projectRootPath;
		_filePollInterval = filePollInterval ?? DefaultFilePollInterval;
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_filePollInterval, TimeSpan.Zero);
	}

	/// <summary>Metadata for the fixed author/reviewer exchange loop.</summary>
	public static ScenarioBlueprint Blueprint { get; } = new(
		"review-loop",
		"Code/Plan review loop",
		[AuthorRole, ReviewerRole],
		[
			new("send-review-brief", AuthorRole, ReviewerRole, "Publish review task and trigger reviewer", ScenarioStepKind.Send),
			new("capture-review", ReviewerRole, null, "Wait for reviewer response file", ScenarioStepKind.Capture),
			new("check-consensus", ReviewerRole, null, "Completion marker emitted?", ScenarioStepKind.Decision),
			new("send-feedback", ReviewerRole, AuthorRole, "Publish findings task and trigger author", ScenarioStepKind.Send),
			new("capture-author-reply", AuthorRole, null, "Wait for author response file", ScenarioStepKind.Capture),
			new("send-author-return", AuthorRole, ReviewerRole, "Publish author feedback task and trigger reviewer", ScenarioStepKind.Send),
			new("loop", ReviewerRole, AuthorRole, "Next review pass", ScenarioStepKind.LoopBack)
		],
		DefaultMaxIterations: 5,
		DefaultTarget: string.Empty,
		CompletionNoticeRole: AuthorRole);

	/// <summary>Runs one reviewer/author pass through the durable task and response-file protocol.</summary>
	public async Task<bool> RunIterationAsync(
		ScenarioIterationContext context,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(context);

		var reviewerStep = ReviewExchangeDirectory.CreateStep(
			_projectRootPath, context.RunId, context.Iteration, ReviewerRole);
		var authorStep = ReviewExchangeDirectory.CreateStep(
			_projectRootPath, context.RunId, context.Iteration, AuthorRole);
		var previousAuthorStep = context.Iteration > 1
			? ReviewExchangeDirectory.CreateStep(
				_projectRootPath, context.RunId, context.Iteration - 1, AuthorRole)
			: authorStep;

		return context.Iteration == 1
			? await RunFirstPassAsync(
				context,
				reviewerStep,
				authorStep,
				previousAuthorStep,
				cancellationToken).ConfigureAwait(false)
			: await RunSubsequentPassAsync(
				context,
				reviewerStep,
				authorStep,
				previousAuthorStep,
				cancellationToken).ConfigureAwait(false);
	}

	private async Task<bool> RunFirstPassAsync(
		ScenarioIterationContext context,
		ReviewExchangeStepPaths reviewerStep,
		ReviewExchangeStepPaths authorStep,
		ReviewExchangeStepPaths previousAuthorStep,
		CancellationToken cancellationToken)
	{
		(var stopMarkerPrefix, var stopMarkerSuffix) = SplitMarker(_definition.StopMarker);
		Dictionary<string, string> reviewerVariables = new()
		{
			["target"] = context.StartPrompt,
			["authorOutput"] = context.PreviousOutput ?? string.Empty,
			["reviewerInstruction"] = _reviewerInstruction,
			["stopMarkerPrefix"] = stopMarkerPrefix,
			["stopMarkerSuffix"] = stopMarkerSuffix,
			["reviewResultFile"] = reviewerStep.ResponsePath,
			["authorFeedbackFile"] = previousAuthorStep.ResponsePath
		};
		var startPrompt = ScenarioTemplateRenderer.Render(
			_definition.StartPromptTemplate,
			reviewerVariables) + BuildReviewerTaskProtocol(reviewerStep);

		var reviewerOutput = await ExchangeAsync(
			context,
			sendStepId: "send-review-brief",
			captureStepId: "capture-review",
			ReviewerRole,
			reviewerStep,
			startPrompt,
			cancellationToken).ConfigureAwait(false);
		context.SetFinalResult(reviewerOutput);

		if (reviewerOutput.Contains(_definition.StopMarker, StringComparison.Ordinal))
		{
			context.Journal("check-consensus", "Consensus reached", ScenarioJournalLevel.Success);
			return true;
		}

		Dictionary<string, string> authorVariables = new()
		{
			["target"] = context.StartPrompt,
			["reviewerOutput"] = reviewerOutput,
			["reviewResultFile"] = reviewerStep.ResponsePath,
			["authorFeedbackFile"] = authorStep.ResponsePath
		};
		var firstFeedbackPrompt = ScenarioTemplateRenderer.Render(
			_definition.FirstFeedbackTemplate,
			authorVariables) + BuildAuthorTaskProtocol(authorStep);

		var authorOutput = await ExchangeAsync(
			context,
			sendStepId: "send-feedback",
			captureStepId: "capture-author-reply",
			AuthorRole,
			authorStep,
			firstFeedbackPrompt,
			cancellationToken).ConfigureAwait(false);
		context.SetPreviousOutput(authorOutput);
		context.Journal("loop", "Author feedback captured, next review pass");
		return false;
	}

	private async Task<bool> RunSubsequentPassAsync(
		ScenarioIterationContext context,
		ReviewExchangeStepPaths reviewerStep,
		ReviewExchangeStepPaths authorStep,
		ReviewExchangeStepPaths previousAuthorStep,
		CancellationToken cancellationToken)
	{
		var previousOutput = context.PreviousOutput
			?? throw new InvalidOperationException("Author output from the previous pass is required.");

		(var stopMarkerPrefix, var stopMarkerSuffix) = SplitMarker(_definition.StopMarker);
		Dictionary<string, string> reviewerVariables = new()
		{
			["target"] = context.StartPrompt,
			["authorOutput"] = previousOutput,
			["reviewerInstruction"] = _reviewerInstruction,
			["stopMarkerPrefix"] = stopMarkerPrefix,
			["stopMarkerSuffix"] = stopMarkerSuffix,
			["reviewResultFile"] = reviewerStep.ResponsePath,
			["authorFeedbackFile"] = previousAuthorStep.ResponsePath
		};
		var authorReturnPrompt = ScenarioTemplateRenderer.Render(
			_definition.AuthorReturnTemplate,
			reviewerVariables) + BuildReviewerTaskProtocol(reviewerStep);

		var reviewerOutput = await ExchangeAsync(
			context,
			sendStepId: "send-author-return",
			captureStepId: "capture-review",
			ReviewerRole,
			reviewerStep,
			authorReturnPrompt,
			cancellationToken).ConfigureAwait(false);
		context.SetFinalResult(reviewerOutput);

		if (reviewerOutput.Contains(_definition.StopMarker, StringComparison.Ordinal))
		{
			context.Journal("check-consensus", "Consensus reached", ScenarioJournalLevel.Success);
			return true;
		}

		Dictionary<string, string> authorVariables = new()
		{
			["target"] = context.StartPrompt,
			["reviewerOutput"] = reviewerOutput,
			["reviewResultFile"] = reviewerStep.ResponsePath,
			["authorFeedbackFile"] = authorStep.ResponsePath
		};
		var feedbackPrompt = ScenarioTemplateRenderer.Render(
			_definition.FeedbackTemplate,
			authorVariables) + BuildAuthorTaskProtocol(authorStep);

		var authorOutput = await ExchangeAsync(
			context,
			sendStepId: "send-feedback",
			captureStepId: "capture-author-reply",
			AuthorRole,
			authorStep,
			feedbackPrompt,
			cancellationToken).ConfigureAwait(false);
		context.SetPreviousOutput(authorOutput);
		context.Journal("loop", "Author feedback captured, next review pass");
		return false;
	}

	private async Task<string> ExchangeAsync(
		ScenarioIterationContext context,
		string sendStepId,
		string captureStepId,
		string role,
		ReviewExchangeStepPaths step,
		string taskContent,
		CancellationToken cancellationToken)
	{
		await ReviewExchangeDirectory.PublishTaskAsync(
			step,
			taskContent,
			cancellationToken).ConfigureAwait(false);
		var expectedResponse = context.SetExpectedResponse(
			role,
			step.TaskPath,
			step.ResponsePath);
		try
		{
			context.Journal(
				sendStepId,
				$"published task {step.TaskPath}; expecting response {step.ResponsePath}:\n{taskContent}");
			var trigger = $"Read and follow the complete instructions in \"{step.TaskPath}\".";
			var incompleteJournaled = false;
			using var deliveryCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			using var responseCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			TaskCompletionSource<Task> initialDeliveryReady =
				new(TaskCreationOptions.RunContinuationsAsynchronously);
			var responseTask = context.WaitForResponseAsync(
				captureStepId,
				role,
				(watchdogTimeout, waitCancellationToken) =>
					ReviewExchangeDirectory.WaitForCompletedResponseAsync(
						step,
						watchdogTimeout,
						_filePollInterval,
						() =>
						{
							if (incompleteJournaled)
							{
								return;
							}

							incompleteJournaled = true;
							context.Journal(
								captureStepId,
								$"response file exists but is incomplete: {step.ResponsePath}");
						},
						waitCancellationToken),
				responseCancellation.Token,
				async recoveryToken =>
				{
					var initialDelivery = await initialDeliveryReady.Task
						.WaitAsync(recoveryToken).ConfigureAwait(false);
					await initialDelivery.WaitAsync(recoveryToken).ConfigureAwait(false);
					await context.SendAsync(
						sendStepId,
						role,
						trigger,
						recoveryToken).ConfigureAwait(false);
				});
			var deliveryTask = context.SendAsync(
				sendStepId,
				role,
				trigger,
				deliveryCancellation.Token);
			initialDeliveryReady.TrySetResult(deliveryTask);

			string response;
			try
			{
				if (await Task.WhenAny(responseTask, deliveryTask).ConfigureAwait(false) == responseTask)
				{
					deliveryCancellation.Cancel();
					response = await responseTask.ConfigureAwait(false);
					try
					{
						await deliveryTask.ConfigureAwait(false);
					}
					catch (OperationCanceledException) when (deliveryCancellation.IsCancellationRequested)
					{
						// The durable response is authoritative; stop any delivery repair before it
						// can submit the already-completed task again.
					}
				}
				else
				{
					await deliveryTask.ConfigureAwait(false);
					response = await responseTask.ConfigureAwait(false);
				}
			}
			finally
			{
				deliveryCancellation.Cancel();
				responseCancellation.Cancel();
			}

			context.Journal(captureStepId, $"read {step.ResponsePath}:\n{response}");
			return response;
		}
		finally
		{
			context.ClearExpectedResponse(expectedResponse);
		}
	}

	/// <summary>Deletes this run's task and response files after its terminal state is captured.</summary>
	public Task CleanupRunArtifactsAsync(string runId, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ReviewExchangeDirectory.CleanupRun(_projectRootPath, runId);
		return Task.CompletedTask;
	}

	private string BuildReviewerTaskProtocol(ReviewExchangeStepPaths step)
	{
		var protocol = "\n\nWrite your COMPLETE review to the response file and keep the terminal reply to one short confirmation.";

		if (_definition.StartPromptTemplate.Contains(ReviewerInstructionPlaceholder, StringComparison.Ordinal))
		{
			return protocol + BuildCompletionFooterProtocol(step);
		}

		(var prefix, var suffix) = SplitMarker(_definition.StopMarker);
		return protocol
			+ "\n\nCompletion marker policy:\n" + _reviewerInstruction
			+ "\n\nIf the policy allows completion, put the completion marker on its own line inside "
			+ "the review response. Build the marker by concatenating these two text fragments: `"
			+ prefix + "` and `" + suffix + "`. Never write the assembled marker otherwise."
			+ "\n\nWhen you emit the completion marker, rewrite the review response as a final run summary "
			+ "instead of a findings list: totals per severity — how many findings were raised over the "
			+ "whole run, how many were resolved, how many were validly disputed; then one entry per "
			+ "finding with the essence of the issue in STRICTLY one-two sentences and the resolution "
			+ "(or the accepted counter-argument) just as briefly; finish with a list of questions that "
			+ "remain open, if any."
			+ BuildCompletionFooterProtocol(step);
	}

	private static string BuildAuthorTaskProtocol(ReviewExchangeStepPaths step) => "\n\nWrite your COMPLETE reply per finding to the response file and keep the terminal reply "
			+ "to one short confirmation."
			+ BuildCompletionFooterProtocol(step);

	private static string BuildCompletionFooterProtocol(ReviewExchangeStepPaths step) => "\n\nResponse file: `" + step.ResponsePath + "`"
			+ "\nCompletion footer: `" + step.CompletionFooter + "`"
			+ "\nWrite the complete response to the response file and place the completion footer as its final non-empty line. Keep the terminal reply to one short confirmation.";

	private static (string Prefix, string Suffix) SplitMarker(string marker)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(marker);

		var splitIndex = marker.LastIndexOf('_');
		if (splitIndex >= 0 && splitIndex < marker.Length - 1)
		{
			return (marker[..(splitIndex + 1)], marker[(splitIndex + 1)..]);
		}

		splitIndex = Math.Max(1, marker.Length / 2);
		return (marker[..splitIndex], marker[splitIndex..]);
	}
}

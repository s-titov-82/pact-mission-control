using System.Diagnostics.CodeAnalysis;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Starts a review an agent asked for: finds the project, scenario, profile and reviewer, waits
/// for the reviewer to be ready, and rolls the reviewer back when the start fails.
/// </summary>
internal sealed class ReviewLaunchService(
	MainWindowViewModel viewModel,
	AvaloniaScenarioCoordinator scenarioCoordinator,
	ReviewProfileProvider reviewProfiles,
	SessionRuntimeCoordinator runtimeCoordinator,
	ITerminalWebViewHost terminalHost,
	SessionLauncher launcher,
	AgentControlTokenRegistry agentControlTokens,
	Action throwIfRejectingInput)
{
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly AvaloniaScenarioCoordinator _scenarioCoordinator = scenarioCoordinator ?? throw new ArgumentNullException(nameof(scenarioCoordinator));
	private readonly ReviewProfileProvider _reviewProfileProvider = reviewProfiles ?? throw new ArgumentNullException(nameof(reviewProfiles));
	private readonly SessionRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
	private readonly ITerminalWebViewHost _terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
	private readonly SessionLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
	private readonly AgentControlTokenRegistry _agentControlTokens = agentControlTokens ?? throw new ArgumentNullException(nameof(agentControlTokens));
	private readonly Action _throwIfRejectingInput = throwIfRejectingInput ?? throw new ArgumentNullException(nameof(throwIfRejectingInput));

	/// <summary>
	/// Atomically creates a hidden reviewer session and starts its reserved review run.
	/// </summary>
	[SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The registered run is owned and later disposed by MainWindow_viewModel.")]
	public async Task<ReviewStartOutcome> StartAgentRequestedReviewAsync(
		string projectId,
		string authorSessionId,
		RequestReviewRequest request,
		CancellationToken cancellationToken)
	{
		_throwIfRejectingInput();
		using var reservation = _scenarioCoordinator.TryReserveProjectSlot(
			projectId,
			out var conflict);
		if (reservation is null)
		{
			return new ReviewStartOutcome(null, conflict, FailureMessage: null);
		}

		var workspace = _viewModel.Workspaces
			.Concat(_viewModel.PausedWorkspaces)
			.FirstOrDefault(candidate => string.Equals(
				candidate.Id,
				projectId,
				StringComparison.Ordinal));
		if (workspace is null)
		{
			return FailedReviewStart($"Project '{projectId}' was not found.");
		}

		if (!workspace.Sessions.Any(session => string.Equals(
				session.Record.Id,
				authorSessionId,
				StringComparison.Ordinal)))
		{
			return FailedReviewStart("The author session no longer belongs to this project.");
		}

		var definition = _viewModel.ScenarioDefinitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ScenarioId, StringComparison.Ordinal));
		if (definition is null)
		{
			return FailedReviewStart($"Scenario '{request.ScenarioId}' was not found.");
		}

		var profile = _reviewProfileProvider.Current.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ReviewProfileId, StringComparison.Ordinal));
		if (profile is null)
		{
			return FailedReviewStart($"Review profile '{request.ReviewProfileId}' was not found.");
		}

		var reviewerSessionId = Guid.NewGuid().ToString("N");
		var sessionCreated = false;
		var terminalCreated = false;
		try
		{
			var reviewer = await _viewModel.CreateSessionAsync(
				sessionId: reviewerSessionId,
				projectId: projectId,
				kind: profile.Kind,
				title: $"Review · {profile.DisplayName}",
				workingDirectory: workspace.RootPath,
				launchCommand: profile.CommandTemplate,
				resumeCommand: null,
				cancellationToken: cancellationToken,
				workspaceId: workspace.Id,
				select: false);
			sessionCreated = true;
			await _terminalHost.CreateTerminalAsync(reviewerSessionId);
			terminalCreated = true;
			await _launcher.ActivateAsync(
				reviewer,
				startIfNeeded: true,
				preferResumeCommand: false,
				cancellationToken);
			var readiness = await _runtimeCoordinator.WaitForSessionReadyAsync(
				reviewerSessionId,
				cancellationToken);
			if (!readiness.IsReady)
			{
				var rollbackFailure = await RollBackAgentReviewerAsync(
					reviewerSessionId,
					sessionCreated,
					terminalCreated);
				return FailedReviewStart(
					$"The reviewer session could not accept the review ({readiness.StatusLine}); "
						+ "the review was not started."
						+ (rollbackFailure is null ? string.Empty : $" {rollbackFailure}"));
			}

			var reviewerInstructions = definition.ReviewerInstructions
				.FirstOrDefault(instruction => string.Equals(
					instruction.Id,
					definition.DefaultReviewerInstructionId,
					StringComparison.Ordinal))
				?.Text
				?? definition.ReviewerInstructions.FirstOrDefault()?.Text
				?? string.Empty;
			var run = _scenarioCoordinator.StartReservedRun(
				reservation,
				definition,
				workspace,
				new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["author"] = authorSessionId,
					["reviewer"] = reviewerSessionId
				},
				request.Target,
				request.MaxIterations ?? definition.MaxIterations,
				reviewerInstructions);
			return new ReviewStartOutcome(run.RunId, Conflict: null, FailureMessage: null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await RollBackAgentReviewerAsync(
				reviewerSessionId,
				sessionCreated,
				terminalCreated);
			throw;
		}
		catch (Exception exception)
		{
			var rollbackFailure = await RollBackAgentReviewerAsync(
				reviewerSessionId,
				sessionCreated,
				terminalCreated);
			var message = rollbackFailure is null
				? $"Review start failed: {exception.Message}"
				: $"Review start failed: {exception.Message}. Rollback: {rollbackFailure}";
			return FailedReviewStart(message);
		}
	}

	private async Task<string?> RollBackAgentReviewerAsync(
		string sessionId,
		bool sessionCreated,
		bool terminalCreated)
	{
		List<string> failures = [];
		try
		{
			await _runtimeCoordinator.StopAsync(sessionId);
		}
		catch (Exception exception)
		{
			failures.Add($"process stop failed: {exception.Message}");
		}

		if (terminalCreated)
		{
			try
			{
				await _terminalHost.DisposeTerminalAsync(sessionId);
			}
			catch (Exception exception)
			{
				failures.Add($"terminal disposal failed: {exception.Message}");
			}
		}

		if (sessionCreated)
		{
			try
			{
				await _viewModel.RemoveSessionAsync(sessionId, CancellationToken.None);
			}
			catch (Exception exception)
			{
				failures.Add($"session removal failed: {exception.Message}");
			}
		}

		_agentControlTokens.Revoke(sessionId);
		return failures.Count == 0 ? null : string.Join("; ", failures);
	}

	private static ReviewStartOutcome FailedReviewStart(string message) =>
		new(RunId: null, Conflict: null, FailureMessage: message);
}

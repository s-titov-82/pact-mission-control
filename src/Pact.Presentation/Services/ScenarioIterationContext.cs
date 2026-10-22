using Pact.Core.Scenarios;
using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>Supplies one scenario iteration with terminal triggering, response waits, and journaling.</summary>
public sealed class ScenarioIterationContext
{
	private static readonly TimeSpan DeliveryRetryDelay = TimeSpan.FromMilliseconds(250);
	private readonly IScenarioTerminalGateway _gateway;
	private readonly ScenarioRunHandle _handle;
	private readonly IReadOnlyDictionary<string, string> _roleBindings;
	private readonly TimeSpan _stepWatchdogTimeout;

	internal ScenarioIterationContext(
		int iteration,
		string startPrompt,
		string? previousOutput,
		IReadOnlyDictionary<string, string> roleBindings,
		IScenarioTerminalGateway gateway,
		ScenarioRunHandle handle,
		TimeSpan stepWatchdogTimeout)
	{
		Iteration = iteration;
		StartPrompt = startPrompt;
		PreviousOutput = previousOutput;
		_roleBindings = roleBindings;
		_gateway = gateway;
		_handle = handle;
		_stepWatchdogTimeout = stepWatchdogTimeout;
	}

	/// <summary>One-based index of the current review pass.</summary>
	public int Iteration { get; }

	/// <summary>Identifier of the run this iteration belongs to.</summary>
	public string RunId => _handle.RunId;

	/// <summary>Fully rendered brief supplied when the run started.</summary>
	public string StartPrompt { get; }

	/// <summary>
	/// Response captured by the previous step, with its transport footer removed, or
	/// <see langword="null"/> on the first step.
	/// </summary>
	public string? PreviousOutput { get; private set; }

	/// <summary>Publishes the durable file exchange currently expected from a role.</summary>
	public ScenarioExpectedResponse SetExpectedResponse(
		string role,
		string taskPath,
		string responsePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(role);
		ArgumentException.ThrowIfNullOrWhiteSpace(taskPath);
		ArgumentException.ThrowIfNullOrWhiteSpace(responsePath);

		ScenarioExpectedResponse expected = new(
			Iteration,
			role,
			GetSessionId(role),
			taskPath,
			responsePath);
		_handle.SetExpectedResponse(expected);
		return expected;
	}

	/// <summary>Clears an expected exchange only if it is still the active one.</summary>
	public void ClearExpectedResponse(ScenarioExpectedResponse expected)
	{
		ArgumentNullException.ThrowIfNull(expected);
		_handle.ClearExpectedResponse(expected);
	}

	/// <summary>Sends one scenario trigger to the terminal session bound to the requested role.</summary>
	public async Task SendAsync(string stepId, string role, string prompt, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
		ArgumentException.ThrowIfNullOrWhiteSpace(role);
		ArgumentNullException.ThrowIfNull(prompt);
		var sessionId = GetSessionId(role);
		var label = _handle.GetSessionLabel(sessionId);
		var firstAttempt = true;
		var attempt = 0;
		var lastReportedDelivery = default(PromptDeliveryResult?);

		while (true)
		{
			await _handle.WaitWhileManualPauseAsync(cancellationToken).ConfigureAwait(false);
			await _handle.PauseIfRequestedAsync(stepId, cancellationToken).ConfigureAwait(false);
			_handle.ThrowIfSoftStopRequested();
			_handle.SetCurrentStepId(stepId);
			await _handle.JournalAsync(
				stepId,
				firstAttempt
					? $"sending trigger to {role} [{label}]:\n{prompt}"
					: $"retrying trigger to {role} [{label}]",
				ScenarioJournalLevel.Info,
				cancellationToken).ConfigureAwait(false);
			firstAttempt = false;
			attempt++;
			PromptDeliveryResult delivery;
			try
			{
				delivery = await _gateway.SendPromptAsync(
					sessionId,
					prompt,
					cancellationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception exception)
			{
				await _handle.ReportDiagnosticAsync(
						$"scenario delivery failure run={_handle.RunId} step={stepId} "
							+ $"iteration={Iteration} role={role} session={sessionId} attempt={attempt}",
						exception)
					.ConfigureAwait(false);
				throw;
			}

			if (lastReportedDelivery is not { } previousDelivery
				|| previousDelivery.Outcome != delivery.Outcome
				|| previousDelivery.WriteAttempted != delivery.WriteAttempted
				|| previousDelivery.SubmitAttempted != delivery.SubmitAttempted)
			{
				await _handle.ReportDiagnosticAsync(
						$"scenario delivery run={_handle.RunId} step={stepId} iteration={Iteration} "
							+ $"role={role} session={sessionId} attempt={attempt} outcome={delivery.Outcome} "
							+ $"writeAttempted={delivery.WriteAttempted} submitAttempted={delivery.SubmitAttempted}")
					.ConfigureAwait(false);
				lastReportedDelivery = delivery;
			}

			await _handle.PauseIfRequestedAsync(stepId, cancellationToken).ConfigureAwait(false);
			if (delivery.Outcome is PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Submitted)
			{
				_handle.ResumeAfterDelivery();
				return;
			}

			// Only a delivery that wrote nothing is retried. Once text reached the terminal it is
			// never sent again: the response file decides whether the step finished.
			var nothingWritten = delivery.Outcome == PromptDeliveryOutcome.NotRunning
				|| (delivery.Outcome == PromptDeliveryOutcome.RefusedQuestion && !delivery.WriteAttempted);
			var reason = delivery.Outcome switch
			{
				PromptDeliveryOutcome.RefusedQuestion when !delivery.WriteAttempted =>
					$"the agent is waiting for an answer in its terminal ({delivery.StatusLine}) "
						+ "- nothing was sent; Pact keeps watching the response file and sends the "
						+ "trigger once the question is answered",
				PromptDeliveryOutcome.NotRunning =>
					$"the session is not running ({delivery.StatusLine}) - nothing was sent; Pact "
						+ "keeps watching the response file and sends the trigger once it runs again",
				PromptDeliveryOutcome.RefusedQuestion =>
					$"a question opened after the trigger was typed ({delivery.StatusLine}) - it may "
						+ "still be in the input; answer the question and submit it yourself. Pact never "
						+ "resends it and keeps watching the response file",
				PromptDeliveryOutcome.PasteNotShown =>
					"the trigger never showed in the terminal and was not submitted - type the task "
						+ "path yourself; Pact never resends it and keeps watching the response file",
				_ =>
					"the trigger was submitted but the agent was not seen taking it - check the "
						+ "terminal; Pact never resends it and keeps watching the response file"
			};
			if (!nothingWritten)
			{
				_handle.PauseForUndelivered(stepId, sessionId, reason);
				return;
			}

			await _handle.PauseForBlockedDeliveryAsync(
				stepId,
				sessionId,
				reason,
				DeliveryRetryDelay,
				cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Waits for one durable response while keeping observation active through pause and the
	/// step watchdog. When the watchdog elapses the run pauses for attention and keeps waiting;
	/// it never resends the trigger. It returns a footer-complete response at once and never ends a
	/// pause; <see cref="AcceptResponseAsync"/> decides when the run may use the response.
	/// </summary>
	/// <param name="stepId">Stable identifier of the scenario step awaiting the response.</param>
	/// <param name="role">Scenario role whose terminal and response file are being observed.</param>
	/// <param name="waitForResponseAsync">Creates one bounded wait for the expected response file.</param>
	/// <param name="cancellationToken">Cancels the run wait.</param>
	public async Task<string> WaitForResponseAsync(
		string stepId,
		string role,
		Func<TimeSpan, CancellationToken, Task<string>> waitForResponseAsync,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
		ArgumentException.ThrowIfNullOrWhiteSpace(role);
		ArgumentNullException.ThrowIfNull(waitForResponseAsync);
		var sessionId = GetSessionId(role);
		_handle.SetCurrentStepId(stepId);

		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var interruptedForPause = false;
			string? response = null;
			using var waitCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			_handle.RegisterPauseInterruption(waitCancellation);
			try
			{
				response = await waitForResponseAsync(
					_stepWatchdogTimeout,
					waitCancellation.Token).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				// Only a pause request cancels this wait apart from the run itself. The request may
				// already have been taken by a concurrent delivery, so it is not checked here.
				interruptedForPause = true;
			}
			catch (ScenarioStepTimeoutException)
			{
				_handle.PauseForTimeout(stepId, sessionId);
				continue;
			}
			finally
			{
				_handle.UnregisterPauseInterruption(waitCancellation);
			}

			await _handle.PauseIfRequestedAsync(
				stepId,
				cancellationToken,
				waitForResume: false).ConfigureAwait(false);
			if (interruptedForPause)
			{
				continue;
			}

			return response!;
		}
	}

	/// <summary>
	/// Lets the run use a response that <see cref="WaitForResponseAsync"/> returned. An attention
	/// pause ends; during a manual pause the response is kept and this waits until the user
	/// resumes. Call it only after any delivery of the step's trigger has been stopped, so a
	/// trigger is never sent once its response exists.
	/// </summary>
	/// <param name="stepId">Stable identifier of the step that received the response.</param>
	/// <param name="cancellationToken">Cancels the wait for Resume.</param>
	public Task AcceptResponseAsync(string stepId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
		return _handle.AcceptResponseAsync(stepId, cancellationToken);
	}

	/// <summary>Adds an entry to the run journal.</summary>
	public void Journal(
		string stepId,
		string message,
		ScenarioJournalLevel level = ScenarioJournalLevel.Info)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
		ArgumentNullException.ThrowIfNull(message);

		_handle.SetCurrentStepId(stepId);
		_handle.JournalAsync(stepId, message, level, CancellationToken.None).GetAwaiter().GetResult();
	}

	/// <summary>
	/// Records the response that becomes <see cref="PreviousOutput"/> for the next step.
	/// </summary>
	public void SetPreviousOutput(string output)
	{
		ArgumentNullException.ThrowIfNull(output);

		PreviousOutput = output;
	}

	/// <summary>Publishes the latest reviewer result document as the run's outcome.</summary>
	public void SetFinalResult(string result)
	{
		ArgumentNullException.ThrowIfNull(result);

		_handle.SetFinalResult(result);
	}

	private string GetSessionId(string role)
	{
		if (!_roleBindings.TryGetValue(role, out var sessionId))
		{
			throw new InvalidOperationException($"Scenario role '{role}' is not bound to a terminal session.");
		}

		return sessionId;
	}
}

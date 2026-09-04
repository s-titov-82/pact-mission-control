using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Tests.Services;

public sealed class ScenarioRunServiceTests : IDisposable
{
	private static readonly ScenarioBlueprint SingleRoleBlueprint = new(
		"single-role",
		"Single role",
		["reviewer"],
		[
			new ScenarioStepMetadata("send", "reviewer", null, "Send prompt", ScenarioStepKind.Send),
			new ScenarioStepMetadata("capture", "reviewer", null, "Capture response", ScenarioStepKind.Capture)
		],
		DefaultMaxIterations: 3,
		DefaultTarget: "start");

	private static readonly ScenarioBlueprint Blueprint = SingleRoleBlueprint;

	private static readonly IReadOnlyDictionary<string, string> Bindings =
		new Dictionary<string, string> { ["reviewer"] = "reviewer-session" };

	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _directory => Path.Combine(_temporaryDirectory.Path, "scenario-project");
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	[Test]
	public async Task Start_RunsProgramToCompletedAndKeepsJournalOnlyInMemory()
	{
		FakeGateway gateway = new();
		var service = CreateService(gateway);
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", context.StartPrompt, ct);
			var output = await context.WaitForResponseAsync(
				"capture",
				"reviewer",
				(_, _) => Task.FromResult("done"),
				ct);
			context.SetPreviousOutput(output);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 3);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		service.IsSessionLocked("session-1", out _).ShouldBeFalse();
		gateway.Sent.ShouldBe([("session-1", "start prompt")]);
		program.Contexts.Single().PreviousOutput.ShouldBe("done");
		handle.Journal.ShouldContain(entry => entry.StepId == "send");
		Directory.Exists(_directory).ShouldBeFalse();
	}

	[Test]
	public async Task Start_FinalizesAsMaxIterationsReachedWhenProgramNeverCompletes()
	{
		FakeGateway gateway = new();
		var service = CreateService(gateway);
		ScriptedProgram program = new((context, _) =>
		{
			context.Journal("loop", $"iteration {context.Iteration}");
			return Task.FromResult(false);
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 2);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.MaxIterationsReached);
		program.Contexts.Select(context => context.Iteration).ToArray().ShouldBe([1, 2]);
	}

	[Test]
	public async Task RequestSoftStop_FinalizesAbortedBetweenIterations()
	{
		FakeGateway gateway = new();
		var service = CreateService(gateway);
		ScenarioRunHandle? handle = null;
		ScriptedProgram program = new((context, _) =>
		{
			handle!.RequestSoftStop();
			return Task.FromResult(false);
		});

		handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 5);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Aborted);
		program.Contexts.ShouldHaveSingleItem();
		handle.Journal.ShouldContain(entry => entry.Message == "stopped by user after step");
	}

	[Test]
	public async Task SendAsync_DoesNotSubmitPromptWhenSoftStopWasAlreadyRequested()
	{
		FakeGateway gateway = new();
		var service = CreateService(gateway);
		ScenarioRunHandle? handle = null;
		ScriptedProgram program = new(async (context, ct) =>
		{
			handle!.RequestSoftStop();
			await context.SendAsync("send", "reviewer", "must not send", ct);
			return true;
		});

		handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Aborted);
		gateway.Sent.ShouldBeEmpty();
	}

	[Test]
	public async Task SendAsync_WrittenDeliveryRetriesAutomatically()
	{
		FakeGateway gateway = new();
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Written,
			string.Empty,
			WriteAttempted: true,
			SubmitAttempted: true));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		var service = CreateService(gateway);
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", "read task file", ct);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		handle.Journal.ShouldContain(entry =>
			entry.Message.StartsWith(
				"the trigger was submitted but the agent never started working",
				StringComparison.Ordinal));
		gateway.Sent.Count.ShouldBe(2);
	}

	[Test]
	public async Task SendAsync_DiagnosticsReportOutcomeChangesWithoutPromptText()
	{
		const string prompt = "read sensitive task file";
		FakeGateway gateway = new();
		gateway.DeliveryResults.Enqueue(new(PromptDeliveryOutcome.BlockedByPendingInput));
		gateway.DeliveryResults.Enqueue(new(PromptDeliveryOutcome.BlockedByPendingInput));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		List<(string Phase, Exception? Exception)> diagnostics = [];
		ScenarioRunService service = new(
			gateway,
			reportDiagnosticAsync: (phase, exception) =>
			{
				diagnostics.Add((phase, exception));
				return Task.CompletedTask;
			});
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", prompt, ct);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		diagnostics.Count.ShouldBe(2);
		diagnostics[0].Phase.ShouldContain("scenario delivery");
		diagnostics[0].Phase.ShouldContain($"run={handle.RunId}");
		diagnostics[0].Phase.ShouldContain("step=send");
		diagnostics[0].Phase.ShouldContain("iteration=1");
		diagnostics[0].Phase.ShouldContain("role=reviewer");
		diagnostics[0].Phase.ShouldContain("session=session-1");
		diagnostics[0].Phase.ShouldContain("attempt=1");
		diagnostics[0].Phase.ShouldContain("outcome=BlockedByPendingInput");
		diagnostics[0].Phase.ShouldContain("writeAttempted=False");
		diagnostics[0].Phase.ShouldContain("submitAttempted=False");
		diagnostics[0].Phase.ShouldNotContain(prompt);
		diagnostics[0].Exception.ShouldBeNull();
		diagnostics[1].Phase.ShouldContain("attempt=3");
		diagnostics[1].Phase.ShouldContain("outcome=Confirmed");
		diagnostics[1].Phase.ShouldContain("writeAttempted=True");
		diagnostics[1].Phase.ShouldContain("submitAttempted=True");
		diagnostics[1].Phase.ShouldNotContain(prompt);
		diagnostics[1].Exception.ShouldBeNull();
	}

	[Test]
	public async Task SendAsync_DiagnosticsCaptureGatewayFailureWithoutPromptText()
	{
		const string prompt = "read sensitive task file";
		InvalidOperationException failure = new("terminal input write failed");
		FakeGateway gateway = new();
		gateway.ThrowOnPromptFor("session-1", failure);
		List<(string Phase, Exception? Exception)> diagnostics = [];
		ScenarioRunService service = new(
			gateway,
			reportDiagnosticAsync: (phase, exception) =>
			{
				diagnostics.Add((phase, exception));
				return Task.CompletedTask;
			});
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", prompt, ct);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Failed);
		diagnostics.ShouldHaveSingleItem();
		diagnostics[0].Phase.ShouldContain("scenario delivery failure");
		diagnostics[0].Phase.ShouldContain($"run={handle.RunId}");
		diagnostics[0].Phase.ShouldContain("step=send");
		diagnostics[0].Phase.ShouldContain("iteration=1");
		diagnostics[0].Phase.ShouldContain("role=reviewer");
		diagnostics[0].Phase.ShouldContain("session=session-1");
		diagnostics[0].Phase.ShouldContain("attempt=1");
		diagnostics[0].Phase.ShouldNotContain(prompt);
		diagnostics[0].Exception.ShouldBeSameAs(failure);
	}

	[Test]
	public async Task SendAsync_DiagnosticSinkFailureDoesNotChangeScenarioOutcome()
	{
		FakeGateway gateway = new();
		ScenarioRunService service = new(
			gateway,
			reportDiagnosticAsync: static (_, _) =>
				Task.FromException(new IOException("log unavailable")));
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", "read task file", ct);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task SendAsync_InputRequestJournalsStatusLineAndRetriesAutomatically()
	{
		FakeGateway gateway = new();
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.BlockedByInputRequest,
			"Approve this edit?"));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		var service = CreateService(gateway);
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.SendAsync("send", "reviewer", "read task file", ct);
			return true;
		});

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.Journal.ShouldContain(entry =>
			entry.Message.Contains("Approve this edit?", StringComparison.Ordinal)
			&& entry.Message.Contains("nothing was sent", StringComparison.Ordinal));
		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent.Count.ShouldBe(2);
	}

	[Test]
	public async Task SendAsync_InputRequestRetriesAutomaticallyAfterTheQuestionClears()
	{
		FakeGateway gateway = new();
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.BlockedByInputRequest,
			"Approve this edit?"));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		var service = CreateService(gateway);
		var handle = service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.SendAsync("send", "reviewer", "read task file", ct);
				return true;
			}),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent.Count.ShouldBe(2);
	}

	[Test]
	public async Task Abort_MidWaitSendsEscapeAndFinalizesAborted()
	{
		FakeGateway gateway = new();
		TaskCompletionSource responseStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> response = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = CreateService(gateway);
		ScriptedProgram program = new(async (context, ct) =>
		{
			await context.WaitForResponseAsync(
				"capture",
				"reviewer",
				(_, waitCancellationToken) =>
				{
					responseStarted.TrySetResult();
					return response.Task.WaitAsync(waitCancellationToken);
				},
				ct);
			return true;
		});
		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await responseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		handle.Abort();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Aborted);
		gateway.EscapedSessions.ShouldBe(["session-1"]);
	}

	[Test]
	public async Task WaitForResponseAsync_pauses_on_timeout_and_completed_response_clears_pause()
	{
		FakeGateway gateway = new();
		var attempts = 0;
		TaskCompletionSource paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ScenarioRunService service = new(
			gateway,
			stepWatchdogTimeout: TimeSpan.FromMilliseconds(50));
		var handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, waitCancellationToken) =>
					{
						attempts++;
						return attempts == 1
							? Task.FromException<string>(new ScenarioStepTimeoutException())
							: responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct);
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);
		handle.StateChanged += (_, _) =>
		{
			if (handle.State == ScenarioRunState.Paused)
			{
				paused.TrySetResult();
			}
		};

		await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.StuckSessionId.ShouldBe("reviewer-session");
		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		attempts.ShouldBe(2);
		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task RequestPause_during_response_wait_keeps_observing_and_clears_pause_without_resending()
	{
		FakeGateway gateway = new();
		TaskCompletionSource firstWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		var service = CreateService(gateway);
		var handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.SendAsync("send", "reviewer", "read task file", ct);
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					async (_, waitCancellationToken) =>
					{
						attempts++;
						if (attempts == 1)
						{
							firstWaitStarted.TrySetResult();
							await Task.Delay(Timeout.InfiniteTimeSpan, waitCancellationToken);
						}

						return await responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct);
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);

		await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.RequestPause();
		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		handle.StuckSessionId.ShouldBeNull();
		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		handle.Journal.ShouldContain(entry => entry.Message == "paused by user");
		gateway.Sent.ShouldHaveSingleItem();

		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		attempts.ShouldBe(2);
		gateway.Sent.ShouldHaveSingleItem();
		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task RequestManualPause_reaches_the_next_pause_boundary()
	{
		FakeGateway gateway = new();
		TaskCompletionSource firstWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = CreateService(gateway);
		var handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					async (_, waitCancellationToken) =>
					{
						firstWaitStarted.TrySetResult();
						return await responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct);
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);

		await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		handle.RequestManualPause().ShouldBe(ScenarioPauseRequestStatus.Requested);
		handle.RequestManualPause().ShouldBe(ScenarioPauseRequestStatus.Unchanged);
		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		handle.PauseRequested.ShouldBeFalse();
		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task RequestManualPause_escalates_attention_pause_and_blocks_recovery_write_until_resume()
	{
		TaskCompletionSource<(string SessionId, string Prompt)> sendStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeGateway gateway = new() { SendStarted = sendStarted };
		TaskCompletionSource recoveryEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		ScenarioRunHandle? handle = null;
		var attempts = 0;
		var service = CreateService(gateway);
		handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, waitCancellationToken) =>
					{
						attempts++;
						return attempts == 1
							? Task.FromException<string>(new ScenarioStepTimeoutException())
							: responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct,
					async recoveryToken =>
					{
						recoveryEntered.TrySetResult();
						await context.SendAsync(
							"send",
							"reviewer",
							"read task file",
							recoveryToken);
					});
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);
		handle.StateChanged += (_, _) =>
		{
			if (handle.State == ScenarioRunState.Paused
				&& !handle.UnlockAllSessionsWhilePaused)
			{
				handle.RequestManualPause().ShouldBe(ScenarioPauseRequestStatus.Escalated);
			}
		};

		await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.State.ShouldBe(ScenarioRunState.Paused);
		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		handle.StuckSessionId.ShouldBeNull();
		gateway.Sent.ShouldBeEmpty();

		handle.Resume();
		await sendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task Manual_pause_escalated_during_recovery_send_survives_delivery_confirmation()
	{
		TaskCompletionSource recoverySendStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseRecoverySend =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource recoveryFinished =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeGateway gateway = new()
		{
			DeliveryResult = new(
				PromptDeliveryOutcome.Confirmed,
				WriteAttempted: true,
				SubmitAttempted: true),
			BeforeDeliveryResultAsync = async cancellationToken =>
			{
				recoverySendStarted.TrySetResult();
				await releaseRecoverySend.Task.WaitAsync(cancellationToken);
			}
		};
		var attempts = 0;
		var service = CreateService(gateway);
		var handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, waitCancellationToken) =>
					{
						attempts++;
						return attempts == 1
							? Task.FromException<string>(new ScenarioStepTimeoutException())
							: responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct,
					async recoveryToken =>
					{
						try
						{
							await context.SendAsync(
								"send",
								"reviewer",
								"read task file",
								recoveryToken);
						}
						finally
						{
							recoveryFinished.TrySetResult();
						}
					});
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);

		await recoverySendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.RequestManualPause().ShouldBe(ScenarioPauseRequestStatus.Escalated);
		releaseRecoverySend.SetResult();
		await recoveryFinished.Task.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Paused);
		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		handle.Completion.IsCompleted.ShouldBeFalse();

		responseReady.SetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task Manual_pause_suppresses_watchdog_delivery_recovery_until_response_arrives()
	{
		FakeGateway gateway = new();
		TaskCompletionSource firstWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource watchdogElapsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource replacementWaitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		var service = CreateService(gateway);
		var handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.SendAsync("send", "reviewer", "read task file", ct);
				var response = await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					async (_, waitCancellationToken) =>
					{
						attempts++;
						if (attempts == 1)
						{
							firstWaitStarted.TrySetResult();
							await Task.Delay(Timeout.InfiniteTimeSpan, waitCancellationToken);
						}

						if (attempts == 2)
						{
							watchdogElapsed.TrySetResult();
							throw new ScenarioStepTimeoutException();
						}

						replacementWaitStarted.TrySetResult();
						return await responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct,
					recoveryToken => context.SendAsync(
						"send",
						"reviewer",
						"read task file",
						recoveryToken));
				response.ShouldBe("file response");
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);

		await firstWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.RequestPause();
		await watchdogElapsed.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await replacementWaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Paused);
		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		gateway.Sent.ShouldHaveSingleItem();

		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent.ShouldHaveSingleItem();
	}

	[Test]
	public async Task RequestPause_winning_timeout_race_creates_one_manual_pause()
	{
		FakeGateway gateway = new();
		TaskCompletionSource releaseRace = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<string> responseReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var attempts = 0;
		var pauseTransitions = 0;
		ScenarioRunHandle? handle = null;
		var service = CreateService(gateway);
		handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					async (_, waitCancellationToken) =>
					{
						attempts++;
						if (attempts == 1)
						{
							await releaseRace.Task.WaitAsync(waitCancellationToken);
							handle!.RequestPause();
							throw new ScenarioStepTimeoutException();
						}

						return await responseReady.Task.WaitAsync(waitCancellationToken);
					},
					ct);
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);
		handle.StateChanged += (_, _) =>
		{
			if (handle.State == ScenarioRunState.Paused
				&& handle.UnlockAllSessionsWhilePaused)
			{
				pauseTransitions++;
			}
		};
		releaseRace.TrySetResult();

		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		handle.StuckSessionId.ShouldBeNull();
		handle.Journal.ShouldContain(entry => entry.Message == "paused by user");

		responseReady.TrySetResult("file response");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		pauseTransitions.ShouldBe(1);
		attempts.ShouldBe(2);
		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task RequestPause_during_unconfirmed_delivery_creates_one_manual_pause()
	{
		TaskCompletionSource deliveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeGateway gateway = new()
		{
			BeforeDeliveryResultAsync = ct =>
			{
				deliveryStarted.TrySetResult();
				return releaseDelivery.Task.WaitAsync(ct);
			}
		};
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Written,
			string.Empty,
			WriteAttempted: true,
			SubmitAttempted: true));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		var pauseTransitions = 0;
		ScenarioRunHandle? handle = null;
		var service = CreateService(gateway);
		handle = service.Start(
			Blueprint,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.SendAsync("send", "reviewer", "read task file", ct);
				return true;
			}),
			"project",
			Bindings,
			"target",
			1);
		handle.StateChanged += (_, _) =>
		{
			if (handle.State == ScenarioRunState.Paused
				&& handle.UnlockAllSessionsWhilePaused)
			{
				pauseTransitions++;
			}
		};

		await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		handle.RequestManualPause().ShouldBe(ScenarioPauseRequestStatus.Requested);
		handle.PauseRequested.ShouldBeTrue();
		handle.TryResume().ShouldBeFalse();
		handle.PauseRequested.ShouldBeTrue();
		releaseDelivery.TrySetResult();
		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		handle.UnlockAllSessionsWhilePaused.ShouldBeTrue();
		handle.StuckSessionId.ShouldBeNull();
		handle.Resume();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));

		pauseTransitions.ShouldBe(1);
		gateway.Sent.Count.ShouldBe(2);
		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task ReviewLoop_watchdog_keeps_observing_response_and_clears_pause_without_resume()
	{
		FakeGateway gateway = new();
		ScenarioRunService service = new(
			gateway,
			stepWatchdogTimeout: TimeSpan.FromMilliseconds(50));
		ScenarioDefinition definition = new(
			"review-loop",
			ScenarioKind.ReviewLoop,
			"Review loop",
			1,
			"DONE",
			"target",
			"Review {target}",
			"Fix {reviewerOutput}",
			"Recheck {authorOutput}",
			"Fix {reviewerOutput}",
			[new("strict", "Strict", "Review strictly")],
			"strict");
		ReviewLoopScenarioProgram program = new(
			definition,
			"Review strictly",
			_directory,
			filePollInterval: TimeSpan.FromMilliseconds(10));
		var handle = service.Start(
			ReviewLoopScenarioProgram.Blueprint,
			program,
			"project",
			new Dictionary<string, string>
			{
				[ReviewLoopScenarioProgram.AuthorRole] = "author-session",
				[ReviewLoopScenarioProgram.ReviewerRole] = "reviewer-session"
			},
			"target",
			maxIterations: 1);

		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		(var SessionId, var Prompt) = gateway.Sent.First(sent =>
			sent.SessionId == "reviewer-session");
		SessionId.ShouldBe("reviewer-session");
		var taskPath = ExtractTaskPath(Prompt);
		var runDirectory = Path.GetDirectoryName(taskPath)!;
		Directory.Exists(runDirectory).ShouldBeTrue();
		File.Exists(taskPath).ShouldBeTrue();
		var task = await File.ReadAllTextAsync(taskPath, CancellationToken.None);
		var responsePath = ExtractProtocolValue(task, "Response file");
		var completionFooter = ExtractProtocolValue(task, "Completion footer");

		await File.WriteAllTextAsync(
			responsePath,
			$"review accepted\nDONE\n{completionFooter}",
			CancellationToken.None);
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		handle.FinalResult.ShouldBe("review accepted\nDONE");
		gateway.Sent.Count(sent => sent.SessionId == "reviewer-session").ShouldBe(2);
		gateway.Sent.Count(sent => sent.SessionId == "author-session"
			&& sent.Prompt.Contains("approved", StringComparison.Ordinal)).ShouldBe(1);
		Directory.Exists(runDirectory).ShouldBeFalse();
	}

	[Test]
	public async Task ReviewLoop_manual_pause_during_delivery_accepts_response_without_resume_or_resend()
	{
		TaskCompletionSource deliveryStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeGateway gateway = new()
		{
			DeliveryResult = new(
				PromptDeliveryOutcome.Written,
				WriteAttempted: true,
				SubmitAttempted: true),
			BeforeDeliveryResultAsync = async cancellationToken =>
			{
				deliveryStarted.TrySetResult();
				await releaseDelivery.Task.WaitAsync(cancellationToken);
			}
		};
		ScenarioRunService service = new(
			gateway,
			stepWatchdogTimeout: TimeSpan.FromSeconds(5));
		ScenarioDefinition definition = new(
			"review-loop",
			ScenarioKind.ReviewLoop,
			"Review loop",
			1,
			"DONE",
			"target",
			"Review {target}",
			"Fix {reviewerOutput}",
			"Recheck {authorOutput}",
			"Fix {reviewerOutput}",
			[new("strict", "Strict", "Review strictly")],
			"strict");
		ReviewLoopScenarioProgram program = new(
			definition,
			"Review strictly",
			_directory,
			filePollInterval: TimeSpan.FromMilliseconds(10));
		var handle = service.Start(
			ReviewLoopScenarioProgram.Blueprint,
			program,
			"project",
			new Dictionary<string, string>
			{
				[ReviewLoopScenarioProgram.AuthorRole] = "author-session",
				[ReviewLoopScenarioProgram.ReviewerRole] = "reviewer-session"
			},
			"target",
			maxIterations: 1);

		await deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		(var _, var prompt) = gateway.Sent.ShouldHaveSingleItem();
		var taskPath = ExtractTaskPath(prompt);
		var task = await File.ReadAllTextAsync(taskPath, CancellationToken.None);
		var responsePath = ExtractProtocolValue(task, "Response file");
		var completionFooter = ExtractProtocolValue(task, "Completion footer");
		handle.ExpectedResponse.ShouldBe(new ScenarioExpectedResponse(
			Iteration: 1,
			Role: ReviewLoopScenarioProgram.ReviewerRole,
			SessionId: "reviewer-session",
			TaskPath: taskPath,
			ResponsePath: responsePath));

		handle.RequestPause();
		await File.WriteAllTextAsync(
			responsePath,
			$"review accepted\nDONE\n{completionFooter}",
			CancellationToken.None);
		releaseDelivery.TrySetResult();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		handle.ExpectedResponse.ShouldBeNull();
		gateway.Sent.Count(sent => sent.SessionId == "reviewer-session").ShouldBe(1);
	}

	[Test]
	public async Task ReviewLoop_watchdog_retries_delivery_while_response_observation_stays_active()
	{
		TaskCompletionSource<(string SessionId, string Prompt)> secondSendStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeGateway gateway = new() { SecondSendStarted = secondSendStarted };
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		ScenarioRunService service = new(
			gateway,
			stepWatchdogTimeout: TimeSpan.FromMilliseconds(50));
		ScenarioDefinition definition = new(
			"review-loop",
			ScenarioKind.ReviewLoop,
			"Review loop",
			1,
			"DONE",
			"target",
			"Review {target}",
			"Fix {reviewerOutput}",
			"Recheck {authorOutput}",
			"Fix {reviewerOutput}",
			[new("strict", "Strict", "Review strictly")],
			"strict");
		ReviewLoopScenarioProgram program = new(
			definition,
			"Review strictly",
			_directory,
			filePollInterval: TimeSpan.FromMilliseconds(10));
		var handle = service.Start(
			ReviewLoopScenarioProgram.Blueprint,
			program,
			"project",
			new Dictionary<string, string>
			{
				[ReviewLoopScenarioProgram.AuthorRole] = "author-session",
				[ReviewLoopScenarioProgram.ReviewerRole] = "reviewer-session"
			},
			"target",
			maxIterations: 1);

		await secondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var firstPrompt = gateway.Sent.First(sent => sent.SessionId == "reviewer-session").Prompt;
		var task = await File.ReadAllTextAsync(ExtractTaskPath(firstPrompt), CancellationToken.None);
		var responsePath = ExtractProtocolValue(task, "Response file");
		var completionFooter = ExtractProtocolValue(task, "Completion footer");
		await File.WriteAllTextAsync(
			responsePath,
			$"review accepted\nDONE\n{completionFooter}",
			CancellationToken.None);
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.Sent.Count(sent => sent.SessionId == "reviewer-session").ShouldBe(2);
	}

	[Test]
	public async Task ReviewLoop_watchdog_never_overlaps_recovery_with_initial_delivery()
	{
		TaskCompletionSource firstDeliveryStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseFirstDelivery =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<(string SessionId, string Prompt)> secondSendStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		var deliverySync = new object();
		var activeDeliveries = 0;
		var maximumConcurrentDeliveries = 0;
		var deliveryCalls = 0;
		FakeGateway gateway = new()
		{
			SecondSendStarted = secondSendStarted,
			BeforeDeliveryResultAsync = async cancellationToken =>
			{
				var active = Interlocked.Increment(ref activeDeliveries);
				lock (deliverySync)
				{
					maximumConcurrentDeliveries = Math.Max(maximumConcurrentDeliveries, active);
				}

				try
				{
					if (Interlocked.Increment(ref deliveryCalls) == 1)
					{
						firstDeliveryStarted.TrySetResult();
						await releaseFirstDelivery.Task.WaitAsync(cancellationToken);
					}
				}
				finally
				{
					Interlocked.Decrement(ref activeDeliveries);
				}
			}
		};
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		gateway.DeliveryResults.Enqueue(new(
			PromptDeliveryOutcome.Confirmed,
			WriteAttempted: true,
			SubmitAttempted: true));
		ScenarioRunService service = new(
			gateway,
			stepWatchdogTimeout: TimeSpan.FromMilliseconds(50));
		ScenarioDefinition definition = new(
			"review-loop",
			ScenarioKind.ReviewLoop,
			"Review loop",
			1,
			"DONE",
			"target",
			"Review {target}",
			"Fix {reviewerOutput}",
			"Recheck {authorOutput}",
			"Fix {reviewerOutput}",
			[new("strict", "Strict", "Review strictly")],
			"strict");
		ReviewLoopScenarioProgram program = new(
			definition,
			"Review strictly",
			_directory,
			filePollInterval: TimeSpan.FromMilliseconds(10));
		var handle = service.Start(
			ReviewLoopScenarioProgram.Blueprint,
			program,
			"project",
			new Dictionary<string, string>
			{
				[ReviewLoopScenarioProgram.AuthorRole] = "author-session",
				[ReviewLoopScenarioProgram.ReviewerRole] = "reviewer-session"
			},
			"target",
			maxIterations: 1);
		TaskCompletionSource watchdogAttention =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		handle.StateChanged += (_, _) =>
		{
			if (handle.State == ScenarioRunState.Paused)
			{
				watchdogAttention.TrySetResult();
			}
		};

		await firstDeliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await watchdogAttention.Task.WaitAsync(TimeSpan.FromSeconds(2));
		releaseFirstDelivery.TrySetResult();
		await secondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

		var firstPrompt = gateway.Sent.First(sent => sent.SessionId == "reviewer-session").Prompt;
		var task = await File.ReadAllTextAsync(ExtractTaskPath(firstPrompt), CancellationToken.None);
		var responsePath = ExtractProtocolValue(task, "Response file");
		var completionFooter = ExtractProtocolValue(task, "Completion footer");
		await File.WriteAllTextAsync(
			responsePath,
			$"review accepted\nDONE\n{completionFooter}",
			CancellationToken.None);
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		maximumConcurrentDeliveries.ShouldBe(1);
		handle.State.ShouldBe(ScenarioRunState.Completed);
	}

	[Test]
	public async Task Finalize_ReleasesLockAndCompletesWhenStateChangedHandlerThrows()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = CreateService(gateway);
		var handle = service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram(async (_, ct) =>
			{
				await release.Task.WaitAsync(ct);
				return true;
			}),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);
		handle.StateChanged += (_, _) => throw new InvalidOperationException("dispatcher closed");

		release.SetResult();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		service.IsSessionLocked("session-1", out _).ShouldBeFalse();
	}

	[Test]
	public void Start_ThrowsWhenRequiredRoleIsMissing()
	{
		var service = CreateService(new FakeGateway());

		var ex = Should.Throw<InvalidOperationException>(() => service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram((_, _) => Task.FromResult(true)),
			"project-1",
			new Dictionary<string, string>(),
			"start prompt",
			maxIterations: 1));

		ex.Message.ShouldContain("reviewer");
	}

	[Test]
	public void Start_ThrowsWhenSessionIsDead()
	{
		FakeGateway gateway = new();
		gateway.DeadSessions.Add("session-1");
		var service = CreateService(gateway);

		var ex = Should.Throw<InvalidOperationException>(() => service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram((_, _) => Task.FromResult(true)),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1));

		ex.Message.ShouldContain("session-1");
	}

	[Test]
	public async Task Start_ThrowsWhenBoundSessionAlreadyLocked()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = CreateService(gateway);
		var first = service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram(async (_, ct) =>
			{
				await release.Task.WaitAsync(ct);
				return true;
			}),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);
		service.IsSessionLocked("session-1", out var runId).ShouldBeTrue();
		runId.ShouldBe(first.RunId);

		var ex = Should.Throw<InvalidOperationException>(() => service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram((_, _) => Task.FromResult(true)),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1));

		release.SetResult();
		await first.Completion.WaitAsync(TimeSpan.FromSeconds(5));
		ex.Message.ShouldContain("session-1");
	}

	[Test]
	public async Task ProgramException_FinalizesFailedAndJournalsError()
	{
		var service = CreateService(new FakeGateway());
		ScriptedProgram program = new((_, _) => throw new InvalidOperationException("boom"));

		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Failed);
		handle.Journal.ShouldContain(entry => entry.Level == ScenarioJournalLevel.Error && entry.Message.Contains("boom"));
	}

	[Test]
	public async Task Abort_invokes_program_artifact_cleanup_before_completion()
	{
		FakeGateway gateway = new();
		var service = CreateService(gateway);
		WaitingArtifactProgram program = new();
		var handle = service.Start(
			SingleRoleBlueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);
		await program.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		handle.Abort();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Aborted);
		program.CleanedRunIds.ShouldBe([handle.RunId]);
	}

	[Test]
	public async Task NotifySessionDied_FinalizesOwningRunAsFailed()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var service = CreateService(gateway);
		var handle = service.Start(
			SingleRoleBlueprint,
			new ScriptedProgram(async (_, ct) =>
			{
				await release.Task.WaitAsync(ct);
				return true;
			}),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start prompt",
			maxIterations: 1);

		service.NotifySessionDied("session-1").ShouldBeTrue();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Failed);
		service.IsSessionLocked("session-1", out _).ShouldBeFalse();
		handle.Journal.ShouldContain(entry =>
			entry.Level == ScenarioJournalLevel.Error
			&& entry.Message.Contains("session exited", StringComparison.OrdinalIgnoreCase));
	}

	[Test]
	public async Task Run_SendsCompletionNoticeToAuthorOnConsensus()
	{
		FakeGateway gateway = new();
		var handle = StartNoticeRun(
			new ScenarioRunService(gateway),
			new ScriptedProgram((_, _) => Task.FromResult(true)));

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		gateway.Sent.Count(sent => sent.SessionId == "author-session"
			&& sent.Prompt.Contains("approved", StringComparison.Ordinal)).ShouldBe(1);
	}

	[Test]
	public async Task Run_SendsCompletionNoticeOnceWhenAborted()
	{
		FakeGateway gateway = new();
		WaitingArtifactProgram program = new();
		var handle = StartNoticeRun(new ScenarioRunService(gateway), program);
		await program.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		handle.Abort();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		gateway.Sent.Count(sent => sent.SessionId == "author-session"
			&& sent.Prompt.Contains("stopped", StringComparison.Ordinal)).ShouldBe(1);
	}

	[Test]
	public async Task Run_SkipsCompletionNoticeWhenAuthorSessionIsGone()
	{
		FakeGateway gateway = new();
		WaitingArtifactProgram program = new();
		var handle = StartNoticeRun(new ScenarioRunService(gateway), program);
		await program.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		gateway.SetAlive("author-session", alive: false);

		handle.Abort();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		gateway.Sent.ShouldNotContain(sent => sent.SessionId == "author-session");
	}

	[Test]
	public async Task Run_JournalsDeliveryFailureWhenNoticeIsRejected()
	{
		FakeGateway gateway = new();
		gateway.RejectPromptsFor("author-session");
		var handle = StartNoticeRun(
			new ScenarioRunService(gateway),
			new ScriptedProgram((_, _) => Task.FromResult(true)));

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		handle.Journal.ShouldContain(entry =>
			entry.Level == ScenarioJournalLevel.Warning
			&& entry.Message.Contains("not delivered", StringComparison.Ordinal));
	}

	[Test]
	public async Task Run_FinalizesWhenTheNoticeThrows()
	{
		FakeGateway gateway = new();
		gateway.ThrowOnPromptFor(
			"author-session",
			new InvalidOperationException("terminal exited"));
		var handle = StartNoticeRun(
			new ScenarioRunService(gateway),
			new ScriptedProgram((_, _) => Task.FromResult(true)));

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		handle.Journal.ShouldContain(entry => entry.Level == ScenarioJournalLevel.Warning);
	}

	[Test]
	public async Task Run_FinalizesWhenNoticeDeliveryStalls()
	{
		FakeGateway gateway = new();
		gateway.BlockPromptsFor("author-session");
		var handle = StartNoticeRun(
			new ScenarioRunService(
				gateway,
				completionNoticeTimeout: TimeSpan.FromMilliseconds(50)),
			new ScriptedProgram((_, _) => Task.FromResult(true)));

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		handle.State.ShouldBe(ScenarioRunState.Completed);
		gateway.ReleaseBlockedPrompts("author-session");
	}

	private static ScenarioRunHandle StartNoticeRun(
		ScenarioRunService service,
		IScenarioProgram program)
	{
		ScenarioBlueprint blueprint = new(
			"notice",
			"Notice",
			["author", "reviewer"],
			[],
			DefaultMaxIterations: 1,
			DefaultTarget: "target",
			CompletionNoticeRole: "author");
		return service.Start(
			blueprint,
			program,
			"project-1",
			new Dictionary<string, string>
			{
				["author"] = "author-session",
				["reviewer"] = "reviewer-session"
			},
			"start",
			maxIterations: 1);
	}

	[Test]
	public void NotifySessionDied_ReturnsFalseForUnlockedSession()
	{
		var service = CreateService(new FakeGateway());

		service.NotifySessionDied("missing-session").ShouldBeFalse();
	}

	private static ScenarioRunService CreateService(FakeGateway gateway) =>
		new(gateway);

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

	private static async Task WaitForStateAsync(ScenarioRunHandle handle, ScenarioRunState state)
	{
		if (handle.State == state)
		{
			return;
		}

		TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStateChanged(object? _, EventArgs __)
		{
			if (handle.State == state)
			{
				reached.TrySetResult();
			}
		}

		handle.StateChanged += OnStateChanged;
		try
		{
			if (handle.State == state)
			{
				return;
			}

			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			handle.StateChanged -= OnStateChanged;
		}
	}

	private sealed class FakeGateway : IScenarioTerminalGateway
	{
		public List<(string SessionId, string Prompt)> Sent { get; } = [];
		public List<string> EscapedSessions { get; } = [];
		public HashSet<string> DeadSessions { get; } = [];
		private readonly HashSet<string> _rejectedSessions = [];
		private readonly Dictionary<string, Exception> _promptFailures = [];
		private readonly Dictionary<string, TaskCompletionSource> _blockedPrompts = [];
		public PromptDeliveryResult DeliveryResult { get; set; } = new(
			PromptDeliveryOutcome.Confirmed,
			string.Empty,
			WriteAttempted: true,
			SubmitAttempted: true);
		public Queue<PromptDeliveryResult> DeliveryResults { get; } = new();
		public Func<CancellationToken, Task>? BeforeDeliveryResultAsync { get; init; }
		public TaskCompletionSource<(string SessionId, string Prompt)>? SendStarted { get; init; }
		public TaskCompletionSource<(string SessionId, string Prompt)>? SecondSendStarted { get; init; }

		public async Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Sent.Add((sessionId, prompt));
			SendStarted?.TrySetResult((sessionId, prompt));
			if (Sent.Count == 2)
			{
				SecondSendStarted?.TrySetResult((sessionId, prompt));
			}

			if (BeforeDeliveryResultAsync is not null)
			{
				await BeforeDeliveryResultAsync(cancellationToken);
			}

			if (_blockedPrompts.TryGetValue(sessionId, out var blocked))
			{
				await blocked.Task.WaitAsync(cancellationToken);
			}

			if (_promptFailures.TryGetValue(sessionId, out var failure))
			{
				throw failure;
			}

			return _rejectedSessions.Contains(sessionId)
				? new PromptDeliveryResult(PromptDeliveryOutcome.BlockedByPendingInput)
				: DeliveryResults.TryDequeue(out var queued) ? queued : DeliveryResult;
		}

		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken)
		{
			EscapedSessions.Add(sessionId);
			return Task.CompletedTask;
		}

		public bool IsSessionAlive(string sessionId) => !DeadSessions.Contains(sessionId);

		public string GetSessionLabel(string sessionId) => sessionId;

		public void SetAlive(string sessionId, bool alive)
		{
			if (alive)
			{
				DeadSessions.Remove(sessionId);
			}
			else
			{
				DeadSessions.Add(sessionId);
			}
		}

		public void RejectPromptsFor(string sessionId) => _rejectedSessions.Add(sessionId);

		public void ThrowOnPromptFor(string sessionId, Exception exception) =>
			_promptFailures[sessionId] = exception;

		public void BlockPromptsFor(string sessionId) =>
			_blockedPrompts[sessionId] =
				new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		public void ReleaseBlockedPrompts(string sessionId) =>
			_blockedPrompts.GetValueOrDefault(sessionId)?.TrySetResult();
	}

	private sealed class ScriptedProgram(
		Func<ScenarioIterationContext, CancellationToken, Task<bool>> runAsync) : IScenarioProgram
	{
		private readonly Func<ScenarioIterationContext, CancellationToken, Task<bool>> _runAsync = runAsync;

		public List<ScenarioIterationContext> Contexts { get; } = [];

		public Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken)
		{
			Contexts.Add(context);
			return _runAsync(context, cancellationToken);
		}
	}

	private sealed class WaitingArtifactProgram : IScenarioProgram, IScenarioRunArtifactCleaner
	{
		public TaskCompletionSource Started { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<string> CleanedRunIds { get; } = [];

		public async Task<bool> RunIterationAsync(
			ScenarioIterationContext context,
			CancellationToken cancellationToken)
		{
			Started.TrySetResult();
			await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
			return false;
		}

		public Task CleanupRunArtifactsAsync(string runId, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			CleanedRunIds.Add(runId);
			return Task.CompletedTask;
		}
	}
}

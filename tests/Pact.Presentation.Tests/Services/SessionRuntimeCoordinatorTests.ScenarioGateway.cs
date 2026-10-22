using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed partial class SessionRuntimeCoordinatorTests
{
	[Test]
	public async Task A_trigger_is_delivered_as_the_scenario_owner_without_starting()
	{
		var (coordinator, backend, host, facts, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		facts.Locked = true;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));
		RuntimeScenarioGateway gateway = new(coordinator);

		var result = await gateway.SendPromptAsync("session-1", "review this", CancellationToken.None);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task Delivery_to_an_exited_session_reports_not_running()
	{
		var (coordinator, backend, _, facts, controller) = await CreateDeliverySessionAsync();
		facts.Start = static _ => throw new InvalidOperationException("no start expected");
		coordinator.GetOrCreateRuntime("session-1").DetachController();
		RuntimeScenarioGateway gateway = new(coordinator);

		var result = await gateway.SendPromptAsync("session-1", "review this", CancellationToken.None);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.NotRunning);
		result.WriteAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
		await controller.DisposeAsync();
	}

	[Test]
	public async Task Liveness_follows_the_runtime_controller()
	{
		var (coordinator, _, _, _, controller) = await CreateDeliverySessionAsync();
		RuntimeScenarioGateway gateway = new(coordinator);

		gateway.IsSessionAlive("session-1").ShouldBeTrue();
		coordinator.GetOrCreateRuntime("session-1").DetachController();
		gateway.IsSessionAlive("session-1").ShouldBeFalse();
		gateway.IsSessionAlive("missing").ShouldBeFalse();
		await controller.DisposeAsync();
	}
}

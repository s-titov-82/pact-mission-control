using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlNotificationHubTests
{
	[Test]
	public async Task PublishToolsListChanged_reaches_each_ordinary_subscriber_once()
	{
		AgentControlNotificationHub hub = new();
		await using var first = hub.Subscribe(
			new AgentControlCaller("session-1", IsOrchestrator: false));
		await using var second = hub.Subscribe(
			new AgentControlCaller("session-2", IsOrchestrator: false));

		hub.PublishToolsListChanged();

		(await first.Reader.ReadAsync()).ShouldBe(
			AgentControlNotificationHub.ToolsListChangedJson);
		(await second.Reader.ReadAsync()).ShouldBe(
			AgentControlNotificationHub.ToolsListChangedJson);
	}

	[Test]
	public void PublishToolsListChanged_does_not_queue_for_orchestrator()
	{
		AgentControlNotificationHub hub = new();
		using var subscription = hub.Subscribe(
			new AgentControlCaller(SessionId: null, IsOrchestrator: true));

		hub.PublishToolsListChanged();

		subscription.Reader.TryRead(out _).ShouldBeFalse();
	}

	[Test]
	public void Repeated_changes_coalesce_to_one_pending_notification()
	{
		AgentControlNotificationHub hub = new();
		using var subscription = hub.Subscribe(
			new AgentControlCaller("session-1", IsOrchestrator: false));

		hub.PublishToolsListChanged();
		hub.PublishToolsListChanged();

		subscription.Reader.TryRead(out _).ShouldBeTrue();
		subscription.Reader.TryRead(out _).ShouldBeFalse();
	}

	[Test]
	public void Disposing_one_subscription_removes_only_that_subscriber()
	{
		AgentControlNotificationHub hub = new();
		var first = hub.Subscribe(new AgentControlCaller("session-1", IsOrchestrator: false));
		using var second = hub.Subscribe(
			new AgentControlCaller("session-2", IsOrchestrator: false));
		first.Dispose();

		hub.PublishToolsListChanged();

		first.Reader.TryRead(out _).ShouldBeFalse();
		second.Reader.TryRead(out _).ShouldBeTrue();
	}

	[Test]
	public async Task Complete_ends_all_readers()
	{
		AgentControlNotificationHub hub = new();
		using var subscription = hub.Subscribe(
			new AgentControlCaller("session-1", IsOrchestrator: false));

		hub.Complete();

		(await subscription.Reader.WaitToReadAsync()).ShouldBeFalse();
	}
}

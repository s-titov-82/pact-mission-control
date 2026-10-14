using Pact.App.Avalonia.Web;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class ScreenCaptureRequestsTests
{
	[Test]
	public async Task A_completed_request_returns_its_snapshot()
	{
		ScreenCaptureRequests requests = new();
		var (id, pending) = requests.Register();
		var snapshot = TerminalScreenSnapshot.FromText("screen");

		requests.Complete(id, snapshot).ShouldBeTrue();

		(await pending).ShouldBeSameAs(snapshot);
	}

	[Test]
	public void An_answer_for_a_removed_or_unknown_request_is_ignored()
	{
		ScreenCaptureRequests requests = new();
		var (id, _) = requests.Register();
		requests.Remove(id);

		requests.Complete(id, TerminalScreenSnapshot.FromText("late")).ShouldBeFalse();
		requests.Complete(id + 100, null).ShouldBeFalse();
	}
}

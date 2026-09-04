using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class WebMessageThreadRouterTests
{
	[Test]
	public void Route_handles_message_inline_when_already_on_ui_thread()
	{
		List<string> events = [];

		WebMessageThreadRouter.Route(
			hasUiThreadAccess: true,
			handle: () => events.Add("handled"),
			post: _ => events.Add("posted"));

		events.ShouldBe(["handled"]);
	}

	[Test]
	public void Route_posts_message_when_callback_arrives_off_ui_thread()
	{
		List<string> events = [];
		Action? posted = null;

		WebMessageThreadRouter.Route(
			hasUiThreadAccess: false,
			handle: () => events.Add("handled"),
			post: action =>
			{
				posted = action;
				events.Add("posted");
			});

		events.ShouldBe(["posted"]);
		posted.ShouldNotBeNull();

		posted();

		events.ShouldBe(["posted", "handled"]);
	}
}
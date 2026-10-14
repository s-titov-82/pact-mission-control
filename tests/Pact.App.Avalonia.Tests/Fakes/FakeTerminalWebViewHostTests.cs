using Pact.Core.ScreenVerdictProfiles;

namespace Pact.App.Avalonia.Tests.Fakes;

public sealed class FakeTerminalWebViewHostTests
{
	[Test]
	public void Default_captures_read_as_empty_composers_for_their_agent()
	{
		CodexScreenProfile.Instance.Classify(FakeTerminalWebViewHost.EmptyComposer).PromptIsEmpty.ShouldBe(true);
		ClaudeScreenProfile.Instance.Classify(FakeTerminalWebViewHost.ClaudeEmptyComposer).PromptIsEmpty.ShouldBe(true);
	}
}

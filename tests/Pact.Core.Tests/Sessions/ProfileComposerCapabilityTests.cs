using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;

namespace Pact.Core.Tests.Sessions;

public sealed class ProfileComposerCapabilityTests
{
	[TestCase(AgentKind.Codex, true)]
	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Pwsh, false)]
	[TestCase(AgentKind.Custom, false)]
	public void Only_agents_with_a_recognized_composer_read_it(AgentKind kind, bool expected) =>
		AgentScreenProfileSelector.ForKind(kind).ReadsComposer.ShouldBe(expected);
}

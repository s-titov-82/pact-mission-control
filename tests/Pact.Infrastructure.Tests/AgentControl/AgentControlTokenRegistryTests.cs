using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlTokenRegistryTests
{
	[Test]
	public void TryResolve_ReturnsSessionForIssuedToken()
	{
		AgentControlTokenRegistry registry = new();
		var token = registry.Issue("session-1");

		registry.TryResolve(token, out var sessionId).ShouldBeTrue();
		sessionId.ShouldBe("session-1");
	}

	[Test]
	public void TryResolve_RejectsUnknownToken()
	{
		AgentControlTokenRegistry registry = new();
		registry.Issue("session-1");

		registry.TryResolve("not-a-token", out _).ShouldBeFalse();
	}

	[Test]
	public void TryResolve_RejectsTokenAfterRevoke()
	{
		AgentControlTokenRegistry registry = new();
		var token = registry.Issue("session-1");

		registry.Revoke("session-1");

		registry.TryResolve(token, out _).ShouldBeFalse();
	}

	[Test]
	public void Issue_ReplacesPreviousTokenForSameSession()
	{
		AgentControlTokenRegistry registry = new();
		var first = registry.Issue("session-1");

		var second = registry.Issue("session-1");

		second.ShouldNotBe(first);
		registry.TryResolve(first, out _).ShouldBeFalse();
		registry.TryResolve(second, out _).ShouldBeTrue();
	}

	[Test]
	public void Issue_GivesDistinctTokensToDistinctSessions()
	{
		AgentControlTokenRegistry registry = new();

		registry.Issue("session-1").ShouldNotBe(registry.Issue("session-2"));
	}

	[Test]
	public void TryResolveCaller_reports_an_ordinary_session_without_orchestrator_rights()
	{
		AgentControlTokenRegistry registry = new();
		var token = registry.Issue("session-1");

		registry.TryResolveCaller(token, out var caller).ShouldBeTrue();
		caller.SessionId.ShouldBe("session-1");
		caller.IsOrchestrator.ShouldBeFalse();
	}

	[Test]
	public void TryResolveCaller_reports_the_orchestrator_credential()
	{
		AgentControlTokenRegistry registry = new();
		registry.SetOrchestratorCredential("slot-token");

		registry.TryResolveCaller("slot-token", out var caller).ShouldBeTrue();
		caller.IsOrchestrator.ShouldBeTrue();
		caller.SessionId.ShouldBeNull();
	}

	[Test]
	public void Clearing_the_orchestrator_credential_revokes_it()
	{
		AgentControlTokenRegistry registry = new();
		registry.SetOrchestratorCredential("slot-token");

		registry.SetOrchestratorCredential(null);

		registry.TryResolveCaller("slot-token", out _).ShouldBeFalse();
	}

	[Test]
	public void Setting_the_orchestrator_credential_replaces_the_previous_one()
	{
		AgentControlTokenRegistry registry = new();
		registry.SetOrchestratorCredential("first");

		registry.SetOrchestratorCredential("second");

		registry.TryResolveCaller("first", out _).ShouldBeFalse();
		registry.TryResolveCaller("second", out _).ShouldBeTrue();
	}

	[Test]
	public void TryResolveCaller_rejects_an_unknown_token()
	{
		AgentControlTokenRegistry registry = new();

		registry.TryResolveCaller("nothing", out _).ShouldBeFalse();
	}
}

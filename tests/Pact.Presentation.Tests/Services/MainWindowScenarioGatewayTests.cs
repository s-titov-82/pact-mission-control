using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

public sealed class MainWindowScenarioGatewayTests
{
	[Test]
	[TestCase(true)]
	[TestCase(false)]
	public async Task SendPromptAsync_delegates_exact_trigger_confirmation_and_cancellation_token(
		bool confirmDelivery)
	{
		(string SessionId, string Prompt, bool ConfirmDelivery, CancellationToken CancellationToken)? sent = null;
		var gateway = CreateGateway(
			send: (sessionId, prompt, confirm, cancellationToken) =>
			{
				sent = (sessionId, prompt, confirm, cancellationToken);
				return Task.FromResult(new PromptDeliveryResult(
					PromptDeliveryOutcome.Confirmed,
					string.Empty,
					WriteAttempted: true,
					SubmitAttempted: true));
			});
		using CancellationTokenSource cancellation = new();

		var result = await gateway.SendPromptAsync(
			"session-1",
			"read task file",
			confirmDelivery,
			cancellation.Token);

		sent.ShouldBe(("session-1", "read task file", confirmDelivery, cancellation.Token));
		result.IsConfirmed.ShouldBeTrue();
	}

	[Test]
	public void IsSessionAlive_requires_known_and_active_session()
	{
		var gateway = CreateGateway(active: id => id == "session-1");

		gateway.IsSessionAlive("session-1").ShouldBeTrue();
		gateway.IsSessionAlive("missing").ShouldBeFalse();
	}

	[Test]
	public async Task SendEscapeAsync_delegates_to_escape_path()
	{
		List<string> escaped = [];
		var gateway = CreateGateway(
			sendEscape: sessionId =>
			{
				escaped.Add(sessionId);
				return Task.CompletedTask;
			});

		await gateway.SendEscapeAsync("session-1", CancellationToken.None);

		escaped.ShouldBe(["session-1"]);
	}

	[Test]
	public void GetSessionLabel_returns_title_or_id_fallback()
	{
		var gateway = CreateGateway();

		gateway.GetSessionLabel("session-1").ShouldBe("Task");
		gateway.GetSessionLabel("missing").ShouldBe("missing");
	}

	private static MainWindowScenarioGateway CreateGateway(
		Func<string, string, bool, CancellationToken, Task<PromptDeliveryResult>>? send = null,
		Func<string, SessionViewModel?>? findSession = null,
		Func<string, Task>? sendEscape = null,
		Func<string, bool>? active = null) => new MainWindowScenarioGateway(
			send ?? ((_, _, _, _) => Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true))),
			findSession ?? (id => id == "session-1" ? CreateSession(id) : null),
			sendEscape ?? (_ => Task.CompletedTask),
			active ?? (_ => true));

	private static SessionViewModel CreateSession(string id)
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionViewModel(new SessionRecord(
			id,
			AgentKind.Codex,
			"Task",
			"C:\\Work",
			"codex",
			null,
			SessionStatus.Running,
			now,
			now));
	}
}

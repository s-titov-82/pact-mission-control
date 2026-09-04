using Pact.Presentation.Services;
using Pact.Core.Sessions;

namespace Pact.Presentation.Tests.Services.Scenarios;

internal sealed class FakeScenarioTerminalGateway : IScenarioTerminalGateway
{
	public List<(string SessionId, string Prompt)> Sent { get; } = [];

	public Func<string, string, CancellationToken, Task>? PromptSentAsync { get; set; }
	public PromptDeliveryResult DeliveryResult { get; set; } = new(
		PromptDeliveryOutcome.Confirmed,
		string.Empty,
		WriteAttempted: true,
		SubmitAttempted: true);

	public HashSet<string> DeadSessions { get; } = [];

	public List<string> EscapedSessions { get; } = [];

	public async Task<PromptDeliveryResult> SendPromptAsync(
		string sessionId,
		string prompt,
		bool confirmDelivery,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Sent.Add((sessionId, prompt));
		if (PromptSentAsync is not null)
		{
			await PromptSentAsync(sessionId, prompt, cancellationToken);
		}

		return DeliveryResult;
	}

	public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken)
	{
		EscapedSessions.Add(sessionId);
		return Task.CompletedTask;
	}

	public bool IsSessionAlive(string sessionId) => !DeadSessions.Contains(sessionId);

	public string GetSessionLabel(string sessionId) => sessionId;
}

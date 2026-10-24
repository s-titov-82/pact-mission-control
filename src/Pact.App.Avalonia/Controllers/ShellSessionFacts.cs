using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Controllers;

/// <summary>The shell's answers to what prompt delivery needs to know about a session.</summary>
internal sealed class ShellSessionFacts(
	Func<string, SessionScreenState?> readScreen,
	Func<string, CancellationToken, Task> startAsync) : ISessionFacts
{
	public SessionScreenState? ReadScreen(string sessionId) => readScreen(sessionId);

	public Task StartAsync(string sessionId, CancellationToken cancellationToken) =>
		startAsync(sessionId, cancellationToken);
}

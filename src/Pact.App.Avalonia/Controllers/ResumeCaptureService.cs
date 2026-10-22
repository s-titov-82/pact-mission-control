using Pact.Core.Agents;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Saves the agent's resume command before its session stops, asking the agent to /exit when
/// the command is not already in the recent output.
/// </summary>
internal sealed class ResumeCaptureService(
	SessionRuntimeCoordinator runtimeCoordinator,
	MainWindowViewModel viewModel,
	TimeProvider timeProvider,
	Func<SessionViewModel, Task> showSessionTerminalAsync,
	Func<string, Task> reportStatusAsync)
{
	private static readonly TimeSpan GracefulAgentExitTimeout = TimeSpan.FromSeconds(20);
	private readonly SessionRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	private readonly Func<SessionViewModel, Task> _showSessionTerminalAsync = showSessionTerminalAsync ?? throw new ArgumentNullException(nameof(showSessionTerminalAsync));
	private readonly Func<string, Task> _reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));

	/// <summary>Captures resume commands for the eligible sessions under one shared deadline.</summary>
	public async Task CaptureBeforeStopAsync(
		IEnumerable<SessionViewModel> sessions,
		CancellationToken cancellationToken)
	{
		var eligible = sessions
			.Where(IsResumeCaptureEligible)
			.ToArray();
		using CancellationTokenSource deadline =
			new(GracefulAgentExitTimeout, _timeProvider);
		using var linkedCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				deadline.Token);
		var attempts = eligible
			.Select(session => CaptureResumeCommandUnderDeadlineAsync(
				session,
				new ResumeCaptureCancellation(
					deadline.Token,
					cancellationToken),
				linkedCancellation.Token))
			.ToArray();
		var timedOutSessionIds = await Task.WhenAll(attempts);
		foreach (var sessionId in timedOutSessionIds.OfType<string>())
		{
			await _reportStatusAsync($"Resume capture timed out: {sessionId}");
		}
	}

	private bool IsResumeCaptureEligible(SessionViewModel session) =>
		_runtimeCoordinator.TryGetActiveController(
			session.Record.Id,
			out _,
			out _)
		&& session.Record.Kind is AgentKind.Codex or AgentKind.Claude
		&& !AgentResumeCommandExtractor.IsConcreteResumeCommand(session.Record.ResumeCommand);

	private async Task<string?> CaptureResumeCommandUnderDeadlineAsync(
		SessionViewModel session,
		ResumeCaptureCancellation cancellation,
		CancellationToken cancellationToken)
	{
		try
		{
			if (!_runtimeCoordinator.TryGetActiveController(
					session.Record.Id,
					out var runtime,
					out _))
			{
				return null;
			}

			await _showSessionTerminalAsync(session);
			if (await TrySaveResumeCommandFromRecentOutputAsync(session, runtime, cancellationToken))
			{
				return null;
			}

			await _reportStatusAsync($"Capturing resume session id: {session.Title}");
			if (!await _runtimeCoordinator.SubmitCommandAsync(
					session.Record.Id,
					"/exit",
					TimeSpan.FromMilliseconds(75),
					_timeProvider,
					cancellationToken))
			{
				return null;
			}

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (await TrySaveResumeCommandFromRecentOutputAsync(session, runtime, cancellationToken))
				{
					return null;
				}

				await Task.Delay(
					TimeSpan.FromMilliseconds(100),
					_timeProvider,
					cancellationToken);
			}
		}
		catch (OperationCanceledException) when (
			cancellation.Deadline.IsCancellationRequested
			&& !cancellation.Caller.IsCancellationRequested)
		{
			return session.Record.Id;
		}
	}

	private readonly record struct ResumeCaptureCancellation(
		CancellationToken Deadline,
		CancellationToken Caller);
	private async Task<bool> TrySaveResumeCommandFromRecentOutputAsync(
		SessionViewModel session,
		SessionRuntime runtime,
		CancellationToken cancellationToken)
	{
		var extracted = AgentResumeCommandExtractor.TryExtract(runtime.GetRecentOutput(), session.Record.Kind);
		var resumeId = AgentResumeCommandExtractor.TryGetResumeId(extracted);
		if (string.IsNullOrWhiteSpace(resumeId))
		{
			return false;
		}

		var resumeCommand = string.IsNullOrWhiteSpace(session.Record.ResumeCommand)
			? extracted
			: AgentResumeCommandExtractor.SetResumeCommandId(
				session.Record.ResumeCommand,
				resumeId);
		if (string.IsNullOrWhiteSpace(resumeCommand)
			|| !AgentResumeCommandExtractor.IsConcreteResumeCommand(resumeCommand))
		{
			return false;
		}

		if (!string.Equals(session.Record.ResumeCommand, resumeCommand, StringComparison.Ordinal))
		{
			await _viewModel.UpdateSessionResumeCommandAsync(session.Record.Id, resumeCommand, cancellationToken);
		}
		return true;
	}
}

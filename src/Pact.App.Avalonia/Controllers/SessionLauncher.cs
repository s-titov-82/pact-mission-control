using Pact.App.Avalonia.Lifecycle;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Presentation;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Starts session processes: the launch command and MCP injection for regular sessions, and the
/// one handler wiring every process start shares, the orchestrator's included.
/// </summary>
internal sealed class SessionLauncher(
	SessionRuntimeCoordinator runtimeCoordinator,
	ITerminalWebViewHost terminalHost,
	MainWindowViewModel viewModel,
	AgentControlTokenRegistry agentControlTokens,
	AppPaths appPaths,
	Func<Uri> agentControlAddress,
	bool agentControlEnabled,
	Func<string, IReadOnlyList<string>, Task<string?>> resolveCommandAsync,
	IUiTaskDispatcher uiTaskDispatcher,
	ObservedTaskGroup eventTasks,
	Action<string, string> onOutput,
	Func<string, Task> resetSnapshotBaselineAsync,
	Action<string> notifySessionDied,
	Func<string, Task> reportStatusAsync)
{
	private readonly SessionRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
	private readonly ITerminalWebViewHost _terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly AgentControlTokenRegistry _agentControlTokens = agentControlTokens ?? throw new ArgumentNullException(nameof(agentControlTokens));
	private readonly AppPaths _appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
	private readonly Func<Uri> _agentControlAddress = agentControlAddress ?? throw new ArgumentNullException(nameof(agentControlAddress));
	private readonly Func<string, IReadOnlyList<string>, Task<string?>> _resolveCommandAsync = resolveCommandAsync ?? throw new ArgumentNullException(nameof(resolveCommandAsync));
	private readonly IUiTaskDispatcher _uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
	private readonly ObservedTaskGroup _eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
	private readonly Action<string, string> _onOutput = onOutput ?? throw new ArgumentNullException(nameof(onOutput));
	private readonly Func<string, Task> _resetSnapshotBaselineAsync = resetSnapshotBaselineAsync ?? throw new ArgumentNullException(nameof(resetSnapshotBaselineAsync));
	private readonly Action<string> _notifySessionDied = notifySessionDied ?? throw new ArgumentNullException(nameof(notifySessionDied));
	private readonly Func<string, Task> _reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));

	/// <summary>The Pact skill published at startup; read at each launch, since it is set after construction.</summary>
	public PactSkillPublication SkillPublication { get; set; } = PactSkillPublication.Empty;

	/// <summary>
	/// Resizes a session's live process, or starts one when <paramref name="startIfNeeded"/> asks for it.
	/// </summary>
	public async Task<SessionStartPlan> ActivateAsync(
		SessionViewModel session,
		bool startIfNeeded,
		bool preferResumeCommand,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(session);
		var startPlan = ShellProfileCommandPlanner.GetStartPlan(
			session.Record,
			preferResumeCommand);
		var runtime = _runtimeCoordinator.GetOrCreateRuntime(session.Record.Id);
		if (runtime.TryGetController(out var existingController)
			&& existingController.IsActive)
		{
			(var columns, var rows) = _terminalHost.GetCurrentSize(session.Record.Id);
			await existingController.ResizeAsync(columns, rows);
			return startPlan;
		}
		if (!startIfNeeded)
		{
			return startPlan;
		}

		var injection = CodexEmbeddedMode.Apply(
			session.Record.Kind,
			startPlan.CommandLine,
			CreateAgentLaunchInjection(session.Record));
		var commandLine = await _resolveCommandAsync(startPlan.CommandLine, injection.Arguments);
		if (string.IsNullOrWhiteSpace(commandLine))
		{
			_agentControlTokens.Revoke(session.Record.Id);
			throw new InvalidOperationException("Session has no launch command.");
		}

		(var startColumns, var startRows) = _terminalHost.GetCurrentSize(session.Record.Id);
		TerminalStartOptions options = new(
			commandLine,
			session.Record.WorkingDirectory,
			startColumns,
			startRows,
			injection.EnvironmentVariables);
		try
		{
			await StartProcessAsync(
				session.Record.Id,
				session.Record.Kind,
				options,
				OnControllerExited,
				cancellationToken);
			await _viewModel.UpdateSessionStatusAsync(session.Record.Id, SessionStatus.Running, cancellationToken);
			_viewModel.TerminalTabStatuses.OnSessionStarted(
				session.Record.Id,
				startPlan.Mode,
				DateTimeOffset.UtcNow);
			return startPlan;
		}
		catch
		{
			await _runtimeCoordinator.StopAsync(session.Record.Id);
			_agentControlTokens.Revoke(session.Record.Id);
			throw;
		}
	}

	/// <summary>
	/// Starts a process with the handlers every session shares: output to the terminal, a snapshot
	/// baseline reset on submit, and input and viewport facts to the tab status.
	/// </summary>
	public Task StartProcessAsync(
		string sessionId,
		AgentKind kind,
		TerminalStartOptions options,
		Action<string, TerminalController> exited,
		CancellationToken cancellationToken)
	{
		void OutputHandler(object? _, string text)
		{
			_onOutput(sessionId, text);
		}

		Task InputWritingHandler(string input)
		{
			return AgentKeyEncoder.IsSubmit(input)
				? _resetSnapshotBaselineAsync(sessionId)
				: Task.CompletedTask;
		}

		void InputWrittenHandler(object? _, string input)
		{
			_viewModel.TerminalTabStatuses.OnUserInput(
				sessionId,
				input,
				DateTimeOffset.UtcNow);
		}

		void ViewportChangedHandler(object? _, TerminalViewportChangedEventArgs args)
		{
			_viewModel.TerminalTabStatuses.OnViewportChanged(
				sessionId,
				args.Columns,
				args.Rows,
				DateTimeOffset.UtcNow);
		}

		return _runtimeCoordinator.StartAsync(
			sessionId,
			kind,
			options,
			exited,
			OutputHandler,
			InputWritingHandler,
			InputWrittenHandler,
			ViewportChangedHandler,
			cancellationToken);
	}

	private LaunchInjection CreateAgentLaunchInjection(SessionRecord session)
	{
		_agentControlTokens.Revoke(session.Id);
		var argumentTemplate = agentControlEnabled
			? AgentControlArgumentTemplates.For(session.Kind)
			: null;
		IReadOnlyList<string> instructionArguments =
			PactInstructionComposer.BuildArguments(
				session.Kind,
				agentControlEnabled,
				SkillPublication);
		if (argumentTemplate is not { Count: > 0 })
		{
			return new LaunchInjection(
				instructionArguments,
				new Dictionary<string, string>(StringComparer.Ordinal));
		}

		var token = _agentControlTokens.Issue(session.Id);
		try
		{
			return AgentControlLaunchInjection.Create(
				argumentTemplate,
				instructionArguments,
				_appPaths.AgentControlDirectory,
				session.Id,
				_agentControlAddress(),
				token);
		}
		catch
		{
			_agentControlTokens.Revoke(session.Id);
			throw;
		}
	}

	private void OnControllerExited(string sessionId, TerminalController controller) =>
		_eventTasks.TryRun(
			"terminal-exit",
			() => HandleControllerExitedAsync(sessionId, controller),
			exception => _reportStatusAsync(
				$"Terminal exit cleanup failed: {exception.Message}"));

	private async Task HandleControllerExitedAsync(
		string sessionId,
		TerminalController controller)
	{
		if (!await _runtimeCoordinator.HandleControllerExitedAsync(
				sessionId,
				controller))
		{
			return;
		}

		_agentControlTokens.Revoke(sessionId);
		_notifySessionDied(sessionId);
		await _uiTaskDispatcher.InvokeAsync(
			() => _viewModel.UpdateSessionStatusAsync(
				sessionId,
				SessionStatus.Exited,
				CancellationToken.None));
	}
}

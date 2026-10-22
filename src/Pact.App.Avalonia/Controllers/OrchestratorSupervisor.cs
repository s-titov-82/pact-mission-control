using System.Diagnostics.CodeAnalysis;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Agents;
using Pact.Core.Orchestrator;
using Pact.Core.Presentation;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Owns the orchestrator slot: applying its settings, starting and stopping its process,
/// restarting it after an unexpected exit, and delivering workstation lock prompts.
/// </summary>
[SuppressMessage(
	"Design",
	"CA1001:Types that own disposable fields should be disposable",
	Justification = "The shell releases the lock monitor through DetachWorkstationLockMonitor at shutdown; the other services are owned by the shell.")]
internal sealed class OrchestratorSupervisor(
	SessionRuntimeCoordinator runtimeCoordinator,
	ITerminalWebViewHost terminalHost,
	MainWindowViewModel viewModel,
	SessionLauncher launcher,
	AgentControlTokenRegistry agentControlTokens,
	Func<Uri> agentControlAddress,
	Func<string, IReadOnlyList<string>, Task<string?>> resolveCommandAsync,
	TimeProvider timeProvider,
	ObservedTaskGroup eventTasks,
	Func<string, Task> reportStatusAsync,
	Func<bool> isShutdownBegun,
	Func<bool> isTerminalHostInitialized)
{
	/// <summary>The orchestrator's fixed session id.</summary>
	public const string SessionId = "pact-orchestrator";

	private readonly SessionRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
	private readonly ITerminalWebViewHost _terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly SessionLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
	private readonly AgentControlTokenRegistry _agentControlTokens = agentControlTokens ?? throw new ArgumentNullException(nameof(agentControlTokens));
	private readonly Func<Uri> _agentControlAddress = agentControlAddress ?? throw new ArgumentNullException(nameof(agentControlAddress));
	private readonly Func<string, IReadOnlyList<string>, Task<string?>> _resolveCommandAsync = resolveCommandAsync ?? throw new ArgumentNullException(nameof(resolveCommandAsync));
	private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	private readonly ObservedTaskGroup _eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
	private readonly Func<string, Task> _reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));
	private readonly Func<bool> _isShutdownBegun = isShutdownBegun ?? throw new ArgumentNullException(nameof(isShutdownBegun));
	private readonly Func<bool> _isTerminalHostInitialized = isTerminalHostInitialized ?? throw new ArgumentNullException(nameof(isTerminalHostInitialized));
	private WorkstationLockMonitor? _workstationLockMonitor;
	private IntPtr _mainWindowHandle;
	private bool _intentionalStop;
	private int _consecutiveFailures;
	private DateTimeOffset _startedAt;

	/// <summary>The settings currently applied to the slot.</summary>
	public OrchestratorRecord Record { get; private set; } = OrchestratorRecord.CreateDefault();

	/// <summary>The orchestrator's session once it has been started at least once.</summary>
	public SessionViewModel? Session { get; private set; }

	public async Task ApplySettingsAsync(
		OrchestratorRecord record,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);
		var restartRequired = Session is not null
			&& (Record.Credential != record.Credential
				|| Record.LaunchCommand != record.LaunchCommand
				|| Record.WorkingDirectory != record.WorkingDirectory);
		if (restartRequired || !record.Enabled || !record.IsProvisioned)
		{
			await StopAsync();
		}

		Record = record;
		_agentControlTokens.SetOrchestratorCredential(
			record.Enabled && record.IsProvisioned ? record.Credential : null);
		RefreshWorkstationLockMonitor();
		if (!record.IsProvisioned)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: false,
				isEnabled: record.Enabled,
				isRunning: false,
				stateText: "Not provisioned");
			return;
		}

		if (!record.Enabled)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: false,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		if (!_isTerminalHostInitialized())
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: "Starting");
		}
		else if (!IsRunning)
		{
			await StartAsync(cancellationToken);
		}
	}

	public void AttachWorkstationLockMonitor(IntPtr windowHandle)
	{
		if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
		{
			return;
		}

		_mainWindowHandle = windowHandle;
		RefreshWorkstationLockMonitor();
	}

	private void RefreshWorkstationLockMonitor()
	{
		var shouldSubscribe = OperatingSystem.IsWindows()
			&& _mainWindowHandle != IntPtr.Zero
			&& Record.Enabled
			&& Record.LockDetectionEnabled;
		if (shouldSubscribe && _workstationLockMonitor is null)
		{
			_workstationLockMonitor = new WorkstationLockMonitor(_mainWindowHandle);
			_workstationLockMonitor.LockStateChanged += OnWorkstationLockStateChanged;
			return;
		}

		if (shouldSubscribe || _workstationLockMonitor is null)
		{
			return;
		}

		_workstationLockMonitor.LockStateChanged -= OnWorkstationLockStateChanged;
		_workstationLockMonitor.Dispose();
		_workstationLockMonitor = null;
	}

	public bool IsRunning =>
		_runtimeCoordinator.TryGetActiveController(
			SessionId,
			out _,
			out _);

	public async Task StartAsync(CancellationToken cancellationToken = default)
	{
		if (!Record.Enabled)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: Record.IsProvisioned,
				isEnabled: false,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		if (!Record.IsProvisioned)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: false,
				isEnabled: true,
				isRunning: false,
				stateText: "Not provisioned");
			return;
		}

		if (IsRunning)
		{
			return;
		}

		try
		{
			var commandLine = await _resolveCommandAsync(Record.LaunchCommand, []);
			if (string.IsNullOrWhiteSpace(commandLine))
			{
				throw new InvalidOperationException(
					"The launch command could not be resolved.");
			}

			var now = DateTimeOffset.UtcNow;
			SessionRecord record = new(
				SessionId,
				AgentKind.Hermes,
				"Orchestrator",
				Record.WorkingDirectory,
				Record.LaunchCommand,
				ResumeCommand: null,
				SessionStatus.Starting,
				now,
				now);
			Session ??= new SessionViewModel(record);
			Session.UpdateRecord(record);
			_viewModel.OrchestratorSlot.AttachSession(Session);
			_viewModel.TerminalTabStatuses.RegisterSession(Session);
			await _terminalHost.CreateTerminalAsync(SessionId);

			(var columns, var rows) = _terminalHost.GetCurrentSize(SessionId);
			Dictionary<string, string> environment = new(StringComparer.Ordinal)
			{
				["PACT_MCP_URL"] = _agentControlAddress().AbsoluteUri,
				["PACT_MCP_TOKEN"] = Record.Credential
			};
			_intentionalStop = false;
			_startedAt = _timeProvider.GetUtcNow();
			await _launcher.StartProcessAsync(
				SessionId,
				record.Kind,
				new TerminalStartOptions(
					commandLine,
					Record.WorkingDirectory,
					columns,
					rows,
					environment),
				OnExited,
				cancellationToken);
			Session.UpdateRecord(record with { Status = SessionStatus.Running });
			_viewModel.TerminalTabStatuses.OnSessionStarted(
				SessionId,
				TerminalStartMode.Normal,
				DateTimeOffset.UtcNow);
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: true,
				stateText: "Running");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			var failure = exception;
			try
			{
				await _runtimeCoordinator.StopAsync(SessionId);
			}
			catch (Exception cleanupException)
			{
				failure = new AggregateException(exception, cleanupException);
			}

			_viewModel.TerminalTabStatuses.RemoveSession(SessionId);
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: $"Failed: {failure.Message}");
		}
	}

	public async Task StopAsync()
	{
		_intentionalStop = true;
		await _runtimeCoordinator.StopAsync(SessionId);
		_viewModel.TerminalTabStatuses.RemoveSession(SessionId);
		Session?.UpdateRecord(
			Session.Record with { Status = SessionStatus.Stopped });

		_viewModel.OrchestratorSlot.Apply(
			isProvisioned: Record.IsProvisioned,
			isEnabled: Record.Enabled,
			isRunning: false,
			stateText: Record.IsProvisioned ? "Stopped" : "Not provisioned");
	}

	private void OnExited(string sessionId, TerminalController controller) =>
		_eventTasks.TryRun(
			"orchestrator-exit",
			() => HandleExitedAsync(controller),
			exception => _reportStatusAsync(
				$"Orchestrator exit cleanup failed: {exception.Message}"));

	private async Task HandleExitedAsync(TerminalController controller)
	{
		if (!await _runtimeCoordinator.HandleControllerExitedAsync(
				SessionId,
				controller))
		{
			return;
		}

		_viewModel.TerminalTabStatuses.RemoveSession(SessionId);
		var intentional = _intentionalStop;
		_intentionalStop = false;
		if (intentional || _isShutdownBegun() || !Record.Enabled)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: Record.Enabled,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		var ranFor = _timeProvider.GetUtcNow() - _startedAt;
		_consecutiveFailures = ranFor >= TimeSpan.FromMinutes(1)
			? 0
			: _consecutiveFailures + 1;
		var delay = OrchestratorRestartPolicy.NextDelay(
			_consecutiveFailures,
			ranFor);
		if (delay is null)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: "Failed: restart budget exhausted");
			return;
		}

		_viewModel.OrchestratorSlot.Apply(
			isProvisioned: true,
			isEnabled: true,
			isRunning: false,
			stateText: delay == TimeSpan.Zero
				? "Restarting"
				: $"Restarting in {delay.Value.TotalSeconds:0}s");
		await Task.Delay(delay.Value, _timeProvider, CancellationToken.None);
		if (!_isShutdownBegun() && Record.Enabled)
		{
			await StartAsync(CancellationToken.None);
		}
	}

	private void OnWorkstationLockStateChanged(object? sender, bool locked)
	{
		_eventTasks.TryRun(
			"orchestrator-lock-state",
			() => DeliverWorkstationLockPromptAsync(locked),
			exception => _reportStatusAsync(
				$"Orchestrator lock prompt failed: {exception.Message}"));
	}

	public async Task DeliverWorkstationLockPromptAsync(bool locked)
	{
		if (!WorkstationLockPolicy.TryBuildPrompt(
				Record,
				locked,
				out var prompt))
		{
			return;
		}

		if (Session is null || !IsRunning)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: Record.IsProvisioned,
				isEnabled: Record.Enabled,
				isRunning: false,
				stateText: locked
					? "Stopped — lock prompt was not delivered"
					: "Stopped — unlock prompt was not delivered");
			return;
		}

		var result = await _runtimeCoordinator.DeliverPromptAsync(
			Session.Record.Id,
			prompt,
			submit: true,
			PromptSender.Outsider,
			startIfNotRunning: false,
			CancellationToken.None);
		if (PromptDeliveryText.DescribeUndelivered(result) is { } reason)
		{
			_viewModel.OrchestratorSlot.Apply(
				isProvisioned: Record.IsProvisioned,
				isEnabled: Record.Enabled,
				isRunning: true,
				stateText: $"{(locked ? "Lock" : "Unlock")} prompt: {reason}");
		}
	}

	public Task<PromptDeliveryResult> SendMessageAsync(
		SessionViewModel target,
		string text,
		CancellationToken cancellationToken) =>
		_runtimeCoordinator.DeliverPromptAsync(
			target.Record.Id,
			text,
			submit: true,
			PromptSender.Outsider,
			startIfNotRunning: false,
			cancellationToken);

	/// <summary>Starts the slot after a soft restart when it is enabled and provisioned.</summary>
	public async Task<bool> RestoreAsync(CancellationToken cancellationToken)
	{
		if (!Record.Enabled || !Record.IsProvisioned)
		{
			return false;
		}
		if (!IsRunning)
		{
			await StartAsync(cancellationToken);
		}
		return IsRunning;
	}

	/// <summary>Stops listening for workstation lock changes; called once at shutdown.</summary>
	public void DetachWorkstationLockMonitor()
	{
		if (_workstationLockMonitor is not null)
		{
			_workstationLockMonitor.LockStateChanged -= OnWorkstationLockStateChanged;
			_workstationLockMonitor.Dispose();
		}
	}
}

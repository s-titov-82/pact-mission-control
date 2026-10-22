using Pact.Core.Scenarios;

namespace Pact.Presentation.Services;

/// <summary>Coordinates scenario execution, session locking, pause/resume, and run cleanup.</summary>
public sealed class ScenarioRunService
{
	/// <summary>
	/// Default time a single step may wait for its response file before the watchdog pauses the
	/// run so the user can intervene.
	/// </summary>
	public static readonly TimeSpan DefaultStepWatchdogTimeout = TimeSpan.FromMinutes(15);

	/// <summary>Default deadline for the best-effort terminal-state notice.</summary>
	public static readonly TimeSpan DefaultCompletionNoticeTimeout = TimeSpan.FromSeconds(30);

	private readonly Lock _sync = new();
	private readonly IScenarioTerminalGateway _gateway;
	private readonly Func<Exception, Task>? _reportCleanupFailureAsync;
	private readonly Func<string, Exception?, Task>? _reportDiagnosticAsync;
	private readonly TimeSpan _stepWatchdogTimeout;
	private readonly TimeSpan _completionNoticeTimeout;
	private readonly Dictionary<string, string> _sessionLocks = new(StringComparer.Ordinal);
	private readonly List<ScenarioRunHandle> _activeRuns = [];

	/// <summary>Creates a coordinator with optional watchdog and diagnostic hooks.</summary>
	/// <param name="gateway">Transport for inspecting sessions and delivering scenario prompts.</param>
	/// <param name="reportCleanupFailureAsync">Optional sink for artifact cleanup failures.</param>
	/// <param name="stepWatchdogTimeout">Optional timeout before a waiting step requests attention.</param>
	/// <param name="completionNoticeTimeout">Optional budget for the final best-effort notice.</param>
	/// <param name="reportDiagnosticAsync">
	/// Optional best-effort sink for metadata-only delivery diagnostics. The service never sends
	/// prompt, response, status-line, or terminal-screen text to this sink.
	/// </param>
	public ScenarioRunService(
		IScenarioTerminalGateway gateway,
		Func<Exception, Task>? reportCleanupFailureAsync = null,
		TimeSpan? stepWatchdogTimeout = null,
		TimeSpan? completionNoticeTimeout = null,
		Func<string, Exception?, Task>? reportDiagnosticAsync = null)
	{
		ArgumentNullException.ThrowIfNull(gateway);

		_gateway = gateway;
		_reportCleanupFailureAsync = reportCleanupFailureAsync;
		_reportDiagnosticAsync = reportDiagnosticAsync;
		_stepWatchdogTimeout = stepWatchdogTimeout ?? DefaultStepWatchdogTimeout;
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_stepWatchdogTimeout, TimeSpan.Zero);
		_completionNoticeTimeout =
			completionNoticeTimeout ?? DefaultCompletionNoticeTimeout;
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			_completionNoticeTimeout,
			TimeSpan.Zero);
	}

	/// <summary>Snapshot of the runs that have not reached a terminal state.</summary>
	public IReadOnlyList<ScenarioRunHandle> ActiveRuns
	{
		get
		{
			lock (_sync)
			{
				return _activeRuns.ToArray();
			}
		}
	}

	/// <summary>
	/// Starts a run and locks the bound sessions against manual input.
	/// </summary>
	/// <param name="blueprint">Shape of the scenario being run.</param>
	/// <param name="program">Program driving the steps.</param>
	/// <param name="projectId">Project the run belongs to.</param>
	/// <param name="roleBindings">Role name to session id, resolved during setup.</param>
	/// <param name="startPrompt">Fully rendered brief for the first step.</param>
	/// <param name="maxIterations">Review pass budget.</param>
	/// <returns>
	/// A handle for observing and controlling the run. Locked sessions stay visible and
	/// scrollable; only their input is blocked.
	/// </returns>
	/// <exception cref="InvalidOperationException">
	/// A bound session is already locked by another run.
	/// </exception>
	public ScenarioRunHandle Start(
		ScenarioBlueprint blueprint,
		IScenarioProgram program,
		string projectId,
		IReadOnlyDictionary<string, string> roleBindings,
		string startPrompt,
		int maxIterations)
	{
		ArgumentNullException.ThrowIfNull(blueprint);
		ArgumentNullException.ThrowIfNull(program);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentNullException.ThrowIfNull(roleBindings);
		ArgumentNullException.ThrowIfNull(startPrompt);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxIterations);

		blueprint.Validate();
		var runId = Guid.NewGuid().ToString("N");
		Dictionary<string, string> copiedBindings = new(roleBindings, StringComparer.Ordinal);

		lock (_sync)
		{
			foreach (var role in blueprint.Roles)
			{
				if (!copiedBindings.TryGetValue(role, out var sessionId)
					|| string.IsNullOrWhiteSpace(sessionId))
				{
					throw new InvalidOperationException($"Scenario role '{role}' is not bound to a terminal session.");
				}

				if (!_gateway.IsSessionAlive(sessionId))
				{
					throw new InvalidOperationException($"Scenario session '{sessionId}' for role '{role}' is not alive.");
				}

				if (_sessionLocks.TryGetValue(sessionId, out var lockedRunId))
				{
					throw new InvalidOperationException(
						$"Scenario session '{sessionId}' is already locked by run '{lockedRunId}'.");
				}
			}

			foreach (var sessionId in copiedBindings.Values.Distinct(StringComparer.Ordinal))
			{
				_sessionLocks.Add(sessionId, runId);
			}

			ScenarioRunHandle handle = new(
				runId,
				blueprint,
				program,
				copiedBindings,
				startPrompt,
				maxIterations,
				_gateway,
				_reportCleanupFailureAsync,
				_reportDiagnosticAsync,
				_stepWatchdogTimeout,
				_completionNoticeTimeout,
				ReleaseRun);
			_activeRuns.Add(handle);
			handle.Start();
			return handle;
		}
	}

	/// <summary>
	/// Whether a scenario currently holds <paramref name="sessionId"/>'s input lock.
	/// </summary>
	/// <param name="sessionId">Session to test.</param>
	/// <param name="runId">Run holding the lock, when locked.</param>
	public bool IsSessionLocked(string sessionId, out string? runId)
	{
		ArgumentNullException.ThrowIfNull(sessionId);

		lock (_sync)
		{
			return _sessionLocks.TryGetValue(sessionId, out runId);
		}
	}

	/// <summary>
	/// Reports that a session's terminal exited, failing any run bound to it. A run cannot
	/// continue without its terminal, so this ends it rather than waiting for the watchdog.
	/// </summary>
	/// <returns><see langword="true"/> when a run was failed as a result.</returns>
	public bool NotifySessionDied(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		ScenarioRunHandle? handle;
		lock (_sync)
		{
			if (!_sessionLocks.TryGetValue(sessionId, out var runId))
			{
				return false;
			}

			handle = _activeRuns.FirstOrDefault(run =>
				string.Equals(run.RunId, runId, StringComparison.Ordinal));
		}

		return handle is not null
			&& handle.RequestFailure($"session exited: {sessionId}");
	}

	private void ReleaseRun(ScenarioRunHandle handle)
	{
		lock (_sync)
		{
			foreach (var sessionId in handle.RoleBindings.Values.Distinct(StringComparer.Ordinal))
			{
				if (_sessionLocks.TryGetValue(sessionId, out var runId)
					&& string.Equals(runId, handle.RunId, StringComparison.Ordinal))
				{
					_sessionLocks.Remove(sessionId);
				}
			}

			_activeRuns.Remove(handle);
		}
	}
}

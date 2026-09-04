using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using Pact.Core.Scenarios;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// Bindable projection of a running scenario: its state, steps, journal, and the actions the
/// user can take on it.
/// </summary>
/// <remarks>
/// Subscribes to the underlying handle and marshals its events onto the UI thread, so run state
/// can advance on background threads without the panel observing torn updates.
/// </remarks>
public sealed class ScenarioRunViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly ScenarioRunHandle _handle;
	private readonly Action<Action> _dispatch;
	private readonly StringBuilder _journalMarkdown = new();
	private bool _disposed;

	/// <summary>Creates a projection that raises changes on the calling thread.</summary>
	public ScenarioRunViewModel(ScenarioRunHandle handle)
		: this(handle, action => action())
	{
	}

	/// <summary>Creates a projection that raises changes through <paramref name="dispatch"/>.</summary>
	public ScenarioRunViewModel(ScenarioRunHandle handle, Action<Action> dispatch)
	{
		ArgumentNullException.ThrowIfNull(handle);
		ArgumentNullException.ThrowIfNull(dispatch);

		_handle = handle;
		_dispatch = dispatch;
		foreach (var entry in handle.Journal)
		{
			Journal.Add(entry);
			AppendJournalMarkdown(entry);
		}

		foreach (var step in handle.Blueprint.Steps)
		{
			Steps.Add(new ScenarioRunStepViewModel(step));
		}

		_handle.JournalEntryAdded += OnJournalEntryAdded;
		_handle.StateChanged += OnStateChanged;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Identifier of the underlying run.</summary>
	public string RunId => _handle.RunId;
	/// <summary>When this presentation projection observed the run starting.</summary>
	public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
	/// <summary>One-based review pass currently executing.</summary>
	public int CurrentIteration => _handle.CurrentIteration;
	/// <summary>
	/// Tree and journal label. Progress against the iteration limit only means something while
	/// passes remain, so a finished run reports how many it actually executed instead.
	/// </summary>
	public string Title => IsTerminal
		? $"{_handle.Blueprint.Name} (finished, {_handle.CurrentIteration} steps)"
		: $"{_handle.Blueprint.Name} (step {_handle.CurrentIteration}/{_handle.MaxIterations})";
	/// <summary>Shape of the scenario being run.</summary>
	public ScenarioBlueprint Blueprint => _handle.Blueprint;
	/// <summary>Role name to session id for this run.</summary>
	public IReadOnlyDictionary<string, string> RoleBindings => _handle.RoleBindings;
	/// <summary>Current run state.</summary>
	public ScenarioRunState State => _handle.State;
	/// <summary>Step currently executing, or <see langword="null"/> before the first step.</summary>
	public string? CurrentStepId => _handle.CurrentStepId;
	/// <summary>The durable task/response pair currently awaited by the run, when any.</summary>
	public ScenarioExpectedResponse? ExpectedResponse => _handle.ExpectedResponse;
	/// <summary>Whether a manual pause request is retained for the next safe boundary.</summary>
	public bool PauseRequested => _handle.PauseRequested;
	/// <summary>Session the watchdog is waiting on while paused; it alone stays unlocked.</summary>
	public string? StuckSessionId => _handle.StuckSessionId;
	/// <summary>Whether the current pause releases every scenario-bound terminal.</summary>
	public bool UnlockAllSessionsWhilePaused => _handle.UnlockAllSessionsWhilePaused;
	/// <summary>Steps of the blueprint, with the current one highlighted.</summary>
	public ObservableCollection<ScenarioRunStepViewModel> Steps { get; } = [];
	/// <summary>Journal entries recorded so far.</summary>
	public ObservableCollection<ScenarioJournalEntry> Journal { get; } = [];
	/// <summary>Journal rendered as Markdown for the read-only run view.</summary>
	public string JournalMarkdown => _journalMarkdown.ToString();

	// Once the run is terminal, the latest validated reviewer response is the
	// run's outcome document and replaces the journal in the panel;
	// the journal stays reachable through the view toggle.
	/// <summary>Run outcome document, empty until the run is terminal.</summary>
	public string FinalResult => _handle.FinalResult ?? string.Empty;
	/// <summary>Whether a terminal run produced a result worth showing.</summary>
	public bool HasFinalResult => IsTerminal && !string.IsNullOrWhiteSpace(_handle.FinalResult);
	/// <summary>Whether the result view is shown rather than the journal.</summary>
	public bool ShowResultView => HasFinalResult && !IsJournalViewSelected;
	/// <summary>Whether the journal view is shown.</summary>
	public bool ShowJournalView => !ShowResultView;

	/// <summary>Whether the user explicitly chose the journal over the result view.</summary>
	public bool IsJournalViewSelected
	{
		get;
		set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(ShowResultView));
			OnPropertyChanged(nameof(ShowJournalView));
		}
	}
	/// <summary>Completes when the run reaches a terminal state; never faults.</summary>
	public Task Completion => _handle.Completion;
	/// <summary>Whether the run is paused awaiting the user.</summary>
	public bool NeedsAttention => State == ScenarioRunState.Paused;
	/// <summary>Whether the tree row should show the pause icon beside its scenario gear.</summary>
	public bool ShowPauseIcon => State == ScenarioRunState.Paused;
	/// <summary>Whether this run is the item currently presented in the center pane.</summary>
	public bool IsCurrentScenario { get; private set; }
	/// <summary>Whether steps are still executing, including while stopping after the current step.</summary>
	public bool IsRunning => State is ScenarioRunState.Running or ScenarioRunState.StoppingAfterStep;
	/// <summary>Whether the run has finished, however it finished.</summary>
	public bool IsTerminal => State is ScenarioRunState.Completed
		or ScenarioRunState.MaxIterationsReached
		or ScenarioRunState.Aborted
		or ScenarioRunState.Failed;
	/// <summary>Whether a stop-after-current-step can be requested.</summary>
	public bool CanSoftStop => State == ScenarioRunState.Running;
	/// <summary>Whether the run can be paused without ending the current exchange.</summary>
	public bool CanPause => State == ScenarioRunState.Running && !PauseRequested;
	/// <summary>Whether the run can be aborted immediately.</summary>
	public bool CanAbort => State is ScenarioRunState.Running
		or ScenarioRunState.StoppingAfterStep
		or ScenarioRunState.Paused;
	/// <summary>Whether a paused run can be resumed.</summary>
	public bool CanResume => State == ScenarioRunState.Paused;

	/// <summary>Glyph representing the current state in the tree.</summary>
	public string StateGlyph => State switch
	{
		ScenarioRunState.Running => "▶",
		ScenarioRunState.StoppingAfterStep => "▶",
		ScenarioRunState.Paused => "⏸",
		ScenarioRunState.Completed => "✓",
		ScenarioRunState.MaxIterationsReached => "⚠",
		ScenarioRunState.Aborted => "■",
		ScenarioRunState.Failed => "✖",
		_ => "?"
	};

	/// <summary>Asks the run to finish after the current step.</summary>
	public void RequestSoftStop()
	{
		if (CanSoftStop)
		{
			_handle.RequestSoftStop();
		}
	}

	/// <summary>Pauses the run at its current safe exchange boundary.</summary>
	public void RequestPause()
	{
		if (CanPause)
		{
			_handle.RequestManualPause();
		}
	}

	/// <summary>Requests or escalates a manual pause and reports whether the run changed.</summary>
	public ScenarioPauseRequestStatus RequestManualPause() => _handle.RequestManualPause();

	/// <summary>Cancels the run immediately.</summary>
	public void Abort()
	{
		if (CanAbort)
		{
			_handle.Abort();
		}
	}

	/// <summary>Resumes a paused run, which waits again for the same expected response.</summary>
	public void Resume()
	{
		if (CanResume)
		{
			_handle.Resume();
		}
	}

	/// <summary>Attempts to resume an established pause without canceling a pending request.</summary>
	public bool TryResume() => _handle.TryResume();

	/// <summary>Updates whether the tree row represents the currently selected scenario.</summary>
	public void SetCurrentScenario(bool value)
	{
		if (IsCurrentScenario == value)
		{
			return;
		}

		IsCurrentScenario = value;
		OnPropertyChanged(nameof(IsCurrentScenario));
	}

	/// <inheritdoc />
	/// <remarks>Detaches from the handle; it does not abort a still-running scenario.</remarks>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_handle.JournalEntryAdded -= OnJournalEntryAdded;
		_handle.StateChanged -= OnStateChanged;
	}

	private void OnJournalEntryAdded(object? sender, ScenarioJournalEntry entry) => _dispatch(() =>
																						 {
																							 Journal.Add(entry);
																							 AppendJournalMarkdown(entry);
																							 OnPropertyChanged(nameof(JournalMarkdown));
																						 });

	private void AppendJournalMarkdown(ScenarioJournalEntry entry)
	{
		if (_journalMarkdown.Length > 0)
		{
			_journalMarkdown.Append("\n\n");
		}

		_journalMarkdown
			.Append("### ")
			.Append(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"))
			.Append(" · ").Append(entry.Level)
			.Append(" · ").Append(entry.StepId)
			.Append("\n\n")
			.Append(entry.Message);
	}

	private void OnStateChanged(object? sender, EventArgs e) => _dispatch(() =>
																	 {
																		 foreach (var step in Steps)
																		 {
																			 step.SetCurrent(string.Equals(step.Id, CurrentStepId, StringComparison.Ordinal));
																		 }

																		 OnPropertyChanged(nameof(State));
																		 OnPropertyChanged(nameof(Title));
																		 OnPropertyChanged(nameof(StateGlyph));
																		 OnPropertyChanged(nameof(NeedsAttention));
																		 OnPropertyChanged(nameof(ShowPauseIcon));
																		 OnPropertyChanged(nameof(IsRunning));
																		 OnPropertyChanged(nameof(IsTerminal));
																		 OnPropertyChanged(nameof(FinalResult));
																		 OnPropertyChanged(nameof(HasFinalResult));
																		 OnPropertyChanged(nameof(ShowResultView));
																		 OnPropertyChanged(nameof(ShowJournalView));
																		 OnPropertyChanged(nameof(CurrentStepId));
																		 OnPropertyChanged(nameof(ExpectedResponse));
																		 OnPropertyChanged(nameof(PauseRequested));
																		 OnPropertyChanged(nameof(StuckSessionId));
																		 OnPropertyChanged(nameof(UnlockAllSessionsWhilePaused));
																		 OnPropertyChanged(nameof(CanSoftStop));
																		 OnPropertyChanged(nameof(CanPause));
																		 OnPropertyChanged(nameof(CanAbort));
																		 OnPropertyChanged(nameof(CanResume));
																	 });

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// One step of a run's blueprint, highlighted while it is the current step.
/// </summary>
public sealed class ScenarioRunStepViewModel : INotifyPropertyChanged
{

	/// <summary>Creates a step row from its blueprint metadata.</summary>
	public ScenarioRunStepViewModel(ScenarioStepMetadata step)
	{
		ArgumentNullException.ThrowIfNull(step);

		Id = step.Id;
		Text = ScenarioSetupStepRow.FromStep(step).Text;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Step id, unique within the blueprint.</summary>
	public string Id { get; }

	/// <summary>Human-readable step description.</summary>
	public string Text { get; }

	/// <summary>Whether this is the step currently executing.</summary>
	public bool IsCurrent { get; private set; }

	internal void SetCurrent(bool isCurrent)
	{
		if (IsCurrent == isCurrent)
		{
			return;
		}

		IsCurrent = isCurrent;
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
	}
}

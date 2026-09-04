using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Scenarios;
using Pact.Core.Sessions;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// Backs the scenario setup dialog: binds roles to live sessions, edits the review target and
/// reviewer instructions, and validates before the run may start.
/// </summary>
public sealed class ScenarioSetupViewModel : INotifyPropertyChanged
{
	private readonly ScenarioBlueprint _blueprint;
	private readonly ScenarioDefinition _definition;
	private string _target;
	private int _maxIterations;
	private ScenarioReviewerInstruction? _selectedReviewerInstruction;
	private string _reviewerInstructionText;

	/// <summary>Creates the setup model for one scenario definition.</summary>
	public ScenarioSetupViewModel(
		ScenarioBlueprint blueprint,
		ScenarioDefinition definition,
		IReadOnlyList<SessionViewModel> projectSessions)
	{
		ArgumentNullException.ThrowIfNull(blueprint);
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(projectSessions);

		_blueprint = blueprint;
		_definition = definition;
		_target = string.IsNullOrWhiteSpace(definition.DefaultTarget)
			? blueprint.DefaultTarget
			: definition.DefaultTarget;
		_maxIterations = definition.MaxIterations;
		ReviewerInstructionOptions = definition.ReviewerInstructions;
		_selectedReviewerInstruction = ReviewerInstructionOptions.FirstOrDefault(option =>
			string.Equals(option.Id, definition.DefaultReviewerInstructionId, StringComparison.Ordinal))
			?? (ReviewerInstructionOptions.Count > 0 ? ReviewerInstructionOptions[0] : null);
		_reviewerInstructionText = _selectedReviewerInstruction?.Text ?? string.Empty;

		StepRows = blueprint.Steps
			.Select(ScenarioSetupStepRow.FromStep)
			.ToArray();
		RoleBindings = blueprint.Roles
			.Select((role, index) => new RoleBindingViewModel(
				role,
				projectSessions,
				index < projectSessions.Count ? projectSessions[index] : null))
			.ToArray();

		foreach (var roleBinding in RoleBindings)
		{
			roleBinding.PropertyChanged += OnRoleBindingPropertyChanged;
		}
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Scenario display name.</summary>
	public string ScenarioName => _definition.Name;
	/// <summary>Exact text the reviewer emits to declare completion.</summary>
	public string StopMarker => _definition.StopMarker;
	/// <summary>One binding row per role the scenario declares.</summary>
	public IReadOnlyList<RoleBindingViewModel> RoleBindings { get; }
	/// <summary>Steps the scenario will execute.</summary>
	public IReadOnlyList<ScenarioStepMetadata> Steps => _blueprint.Steps;
	/// <summary>Steps rendered as display rows.</summary>
	public IReadOnlyList<ScenarioSetupStepRow> StepRows { get; }
	/// <summary>Selectable reviewer instruction presets.</summary>
	public IReadOnlyList<ScenarioReviewerInstruction> ReviewerInstructionOptions { get; }

	/// <summary>Review scope pointer or pasted text the run operates on.</summary>
	public string Target
	{
		get => _target;
		set
		{
			if (string.Equals(_target, value, StringComparison.Ordinal))
			{
				return;
			}

			_target = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Whether to persist the target as the definition's default for next time.</summary>
	public bool SaveTargetAsDefault
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
		}
	}

	/// <summary>Review pass budget for this run.</summary>
	public int MaxIterations
	{
		get => _maxIterations;
		set
		{
			if (_maxIterations == value)
			{
				return;
			}

			_maxIterations = value;
			OnPropertyChanged();
			NotifyValidationChanged();
		}
	}

	/// <summary>Chosen preset, which seeds <see cref="ReviewerInstructionText"/>.</summary>
	public ScenarioReviewerInstruction? SelectedReviewerInstruction
	{
		get => _selectedReviewerInstruction;
		set
		{
			if (ReferenceEquals(_selectedReviewerInstruction, value))
			{
				return;
			}

			_selectedReviewerInstruction = value;
			_reviewerInstructionText = value?.Text ?? string.Empty;
			OnPropertyChanged();
			OnPropertyChanged(nameof(ReviewerInstructionText));
		}
	}

	/// <summary>
	/// Reviewer instructions actually sent. Seeded from the preset but freely editable, since
	/// reviewer discipline is text rather than a fixed enum.
	/// </summary>
	public string ReviewerInstructionText
	{
		get => _reviewerInstructionText;
		set
		{
			if (string.Equals(_reviewerInstructionText, value, StringComparison.Ordinal))
			{
				return;
			}

			_reviewerInstructionText = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Whether the dialog may start the run.</summary>
	public bool CanRun => ValidationMessage is null;

	/// <summary>Why the run cannot start, or <see langword="null"/> when it can.</summary>
	public string? ValidationMessage
	{
		get
		{
			if (MaxIterations < 1)
			{
				return "Max iterations must be at least 1.";
			}

			var selectedSessions = RoleBindings
				.Select(binding => binding.SelectedSession)
				.ToArray();
			if (selectedSessions.Any(session => session is null))
			{
				return "Bind every role to a running session.";
			}

			var sessions = selectedSessions.OfType<SessionViewModel>().ToArray();
			if (sessions.Any(session => session.Record.Status != SessionStatus.Running))
			{
				return "Selected sessions must be running.";
			}

			if (sessions.Any(session => session.IsLockedByScenario))
			{
				return "Selected sessions must not already be locked by a scenario.";
			}

			if (sessions.Select(session => session.Record.Id).Distinct(StringComparer.Ordinal).Count() != sessions.Length)
			{
				return "Each role must use a distinct running session.";
			}

			return null;
		}
	}

	/// <summary>Materializes the role-to-session map the run is started with.</summary>
	public IReadOnlyDictionary<string, string> BuildRoleBindings()
	{
		if (!CanRun)
		{
			throw new InvalidOperationException(ValidationMessage ?? "Scenario setup is invalid.");
		}

		return RoleBindings.ToDictionary(
			binding => binding.Role,
			binding => binding.SelectedSession!.Record.Id,
			StringComparer.Ordinal);
	}

	private void OnRoleBindingPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (string.Equals(e.PropertyName, nameof(RoleBindingViewModel.SelectedSession), StringComparison.Ordinal))
		{
			NotifyValidationChanged();
		}
	}

	private void NotifyValidationChanged()
	{
		OnPropertyChanged(nameof(CanRun));
		OnPropertyChanged(nameof(ValidationMessage));
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// One step rendered for the setup dialog.
/// </summary>
/// <param name="Text">Display text combining the step kind, roles, and description.</param>
public sealed record ScenarioSetupStepRow(string Text)
{
	/// <summary>Renders a blueprint step into a display row.</summary>
	public static ScenarioSetupStepRow FromStep(ScenarioStepMetadata step)
	{
		ArgumentNullException.ThrowIfNull(step);

		var prefix = step.Kind switch
		{
			ScenarioStepKind.LoopBack => "⟳ ",
			ScenarioStepKind.Decision => "? ",
			_ => string.Empty
		};
		var roleText = step.ToRole is null
			? step.FromRole
			: $"{step.FromRole} → {step.ToRole}";
		return new ScenarioSetupStepRow($"{prefix}{roleText}: {step.Description}");
	}
}

/// <summary>
/// Binds one scenario role to a live terminal session.
/// </summary>
public sealed class RoleBindingViewModel : INotifyPropertyChanged
{
	private SessionViewModel? _selectedSession;

	/// <summary>
	/// Creates a binding row. A preselected session outside the candidate list is ignored, so the
	/// selection can never name a session the run could not reach.
	/// </summary>
	public RoleBindingViewModel(
		string role,
		IReadOnlyList<SessionViewModel> candidates,
		SessionViewModel? selectedSession = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(role);
		ArgumentNullException.ThrowIfNull(candidates);

		Role = role;
		Candidates = new ObservableCollection<SessionViewModel>(candidates);
		_selectedSession = selectedSession is not null && Candidates.Contains(selectedSession)
			? selectedSession
			: null;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Role being bound.</summary>
	public string Role { get; }
	/// <summary>Sessions eligible for this role.</summary>
	public ObservableCollection<SessionViewModel> Candidates { get; }

	/// <summary>Session bound to the role, or <see langword="null"/> when unbound.</summary>
	public SessionViewModel? SelectedSession
	{
		get => _selectedSession;
		set
		{
			if (ReferenceEquals(_selectedSession, value))
			{
				return;
			}

			_selectedSession = value;
			OnPropertyChanged();
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
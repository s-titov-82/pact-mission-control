using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One reviewer instruction nested inside a scenario's reviewerInstructions array.</summary>
public sealed class ReviewerInstructionItemViewModel : SettingsObservableObject
{
	private string _id;
	private string _name;
	private string _text;

	/// <summary>Creates a preset row.</summary>
	public ReviewerInstructionItemViewModel(string id, string name, string text)
	{
		_id = id;
		_name = name;
		_text = text;
	}

	/// <summary>Raised whenever an editable field changes; the owning scenario item marks itself dirty.</summary>
	public event EventHandler? Changed;

	/// <summary>Stable key referenced by the scenario's default instruction id.</summary>
	public string Id
	{
		get => _id;
		set
		{
			if (SetField(ref _id, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>Label shown in the setup dialog's preset picker.</summary>
	public string Name
	{
		get => _name;
		set
		{
			if (SetField(ref _name, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>Instruction text seeded into the dialog; the user may still edit it per run.</summary>
	public string Text
	{
		get => _text;
		set
		{
			if (SetField(ref _text, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}
}

/// <summary>One review-loop scenario tab, backed by an entry in scenarios.json.</summary>
public sealed class ScenarioItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private string _name;
	private string _maxIterationsText;
	private string _stopMarker;
	private string _defaultTarget;
	private string _startPromptTemplate;
	private string _firstFeedbackTemplate;
	private string _authorReturnTemplate;
	private string _feedbackTemplate;
	private string _defaultReviewerInstructionId;
	private ReviewerInstructionItemViewModel? _selectedInstruction;

	/// <summary>Creates an item over its JSON node.</summary>
	public ScenarioItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		KindDisplay = (string?)node["kind"] ?? string.Empty;
		_name = (string?)node["name"] ?? string.Empty;
		_maxIterationsText = node["maxIterations"] is JsonValue maxIterations && maxIterations.TryGetValue(out int parsed)
			? parsed.ToString()
			: string.Empty;
		_stopMarker = (string?)node["stopMarker"] ?? string.Empty;
		_defaultTarget = (string?)node["defaultTarget"] ?? string.Empty;
		_startPromptTemplate = (string?)node["startPromptTemplate"] ?? string.Empty;
		_firstFeedbackTemplate = (string?)node["firstFeedbackTemplate"] ?? string.Empty;
		_authorReturnTemplate = (string?)node["authorReturnTemplate"] ?? string.Empty;
		_feedbackTemplate = (string?)node["feedbackTemplate"] ?? string.Empty;
		_defaultReviewerInstructionId = (string?)node["defaultReviewerInstructionId"] ?? string.Empty;

		ReviewerInstructions = [];
		if (node["reviewerInstructions"] is JsonArray instructions)
		{
			foreach (var instructionNode in instructions.OfType<JsonObject>())
			{
				AttachInstruction(new ReviewerInstructionItemViewModel(
					(string?)instructionNode["id"] ?? string.Empty,
					(string?)instructionNode["name"] ?? string.Empty,
					(string?)instructionNode["text"] ?? string.Empty));
			}
		}

		_selectedInstruction = ReviewerInstructions.Count > 0 ? ReviewerInstructions[0] : null;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Read-only display of the node's raw "kind" value; this section only handles reviewLoop.</summary>
	public string KindDisplay { get; }

	/// <summary>Stable key; also used to persist the default target.</summary>
	public string Id
	{
		get => _id;
		set
		{
			if (SetField(ref _id, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Label shown in the scenarios list.</summary>
	public string Name
	{
		get => _name;
		set
		{
			if (SetField(ref _name, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>String-bound; validated as an integer &gt;= 1 on save.</summary>
	public string MaxIterationsText
	{
		get => _maxIterationsText;
		set
		{
			if (SetField(ref _maxIterationsText, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Exact text the reviewer emits to declare completion; the only accepted stop signal.</summary>
	public string StopMarker
	{
		get => _stopMarker;
		set
		{
			if (SetField(ref _stopMarker, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Review target prefilled in the setup dialog.</summary>
	public string DefaultTarget
	{
		get => _defaultTarget;
		set
		{
			if (SetField(ref _defaultTarget, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Pass 1 brief sent to the reviewer.</summary>
	public string StartPromptTemplate
	{
		get => _startPromptTemplate;
		set
		{
			if (SetField(ref _startPromptTemplate, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Pass 1 findings sent to the author.</summary>
	public string FirstFeedbackTemplate
	{
		get => _firstFeedbackTemplate;
		set
		{
			if (SetField(ref _firstFeedbackTemplate, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Author's reply returned to the reviewer in passes 2..N.</summary>
	public string AuthorReturnTemplate
	{
		get => _authorReturnTemplate;
		set
		{
			if (SetField(ref _authorReturnTemplate, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Follow-up findings sent to the author in passes 2..N.</summary>
	public string FeedbackTemplate
	{
		get => _feedbackTemplate;
		set
		{
			if (SetField(ref _feedbackTemplate, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Reviewer instruction presets offered for this scenario.</summary>
	public ObservableCollection<ReviewerInstructionItemViewModel> ReviewerInstructions { get; }

	/// <summary>Selected preset row in the editor, or <see langword="null"/>.</summary>
	public ReviewerInstructionItemViewModel? SelectedInstruction
	{
		get => _selectedInstruction;
		set => SetField(ref _selectedInstruction, value);
	}

	/// <summary>Preset selected when the setup dialog opens; must match an existing preset.</summary>
	public string DefaultReviewerInstructionId
	{
		get => _defaultReviewerInstructionId;
		set
		{
			if (SetField(ref _defaultReviewerInstructionId, value))
			{
				OnPropertyChanged(nameof(DefaultReviewerInstruction));
				RaiseChanged();
			}
		}
	}

	/// <summary>Preset the default id resolves to, or <see langword="null"/> when it matches none.</summary>
	public ReviewerInstructionItemViewModel? DefaultReviewerInstruction
	{
		get => ReviewerInstructions.FirstOrDefault(instruction =>
			string.Equals(instruction.Id, DefaultReviewerInstructionId, StringComparison.Ordinal));
		set
		{
			if (value is not null)
			{
				DefaultReviewerInstructionId = value.Id;
			}
		}
	}

	/// <summary>Adds a new, empty reviewer instruction and selects it.</summary>
	public void AddInstruction()
	{
		var instruction = new ReviewerInstructionItemViewModel(string.Empty, string.Empty, string.Empty);
		AttachInstruction(instruction);
		SelectedInstruction = instruction;
		RaiseChanged();
	}

	/// <summary>
	/// Removes <paramref name="instruction"/>. No-op when it is the last remaining instruction
	/// (at least one is required; also enforced by validation). Re-points
	/// <see cref="DefaultReviewerInstructionId"/> to the first remaining instruction when the
	/// removed instruction was the default.
	/// </summary>
	public void RemoveInstruction(ReviewerInstructionItemViewModel instruction)
	{
		ArgumentNullException.ThrowIfNull(instruction);

		if (ReviewerInstructions.Count <= 1 || !ReviewerInstructions.Remove(instruction))
		{
			return;
		}

		DetachInstruction(instruction);

		if (ReferenceEquals(SelectedInstruction, instruction))
		{
			SelectedInstruction = ReviewerInstructions.Count > 0 ? ReviewerInstructions[0] : null;
		}

		if (string.Equals(instruction.Id, DefaultReviewerInstructionId, StringComparison.Ordinal))
		{
			DefaultReviewerInstructionId = ReviewerInstructions[0].Id;
		}

		RaiseChanged();
	}

	/// <inheritdoc />
	public override string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Name)
				? Name
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new scenario)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["kind"] = "reviewLoop";
		_node["name"] = Name;
		_node["maxIterations"] = int.TryParse(MaxIterationsText, out var maxIterations) ? maxIterations : 0;
		_node["stopMarker"] = StopMarker;
		_node["defaultTarget"] = DefaultTarget;
		_node["startPromptTemplate"] = StartPromptTemplate;
		_node["firstFeedbackTemplate"] = FirstFeedbackTemplate;
		_node["authorReturnTemplate"] = AuthorReturnTemplate;
		_node["feedbackTemplate"] = FeedbackTemplate;

		var instructions = new JsonArray();
		foreach (var instruction in ReviewerInstructions)
		{
			instructions.Add(new JsonObject
			{
				["id"] = instruction.Id,
				["name"] = instruction.Name,
				["text"] = instruction.Text
			});
		}

		_node["reviewerInstructions"] = instructions;
		_node["defaultReviewerInstructionId"] = DefaultReviewerInstructionId;
	}

	private void AttachInstruction(ReviewerInstructionItemViewModel instruction)
	{
		instruction.Changed += OnInstructionChanged;
		ReviewerInstructions.Add(instruction);
	}

	private void DetachInstruction(ReviewerInstructionItemViewModel instruction)
		=> instruction.Changed -= OnInstructionChanged;

	private void OnInstructionChanged(object? sender, EventArgs e) => RaiseChanged();
}
using System.Text.Json.Nodes;
using Pact.Core.Prompting;
namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One prompt-template tab, backed by an entry in prompt-templates.json.</summary>
public sealed class PromptTemplateItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id, _name, _body;
	private bool _sendByDefault;
	private PromptActionType _type;
	/// <summary>Creates an item over its JSON node.</summary>
	public PromptTemplateItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_name = (string?)node["name"] ?? string.Empty;
		_body = (string?)node["body"] ?? string.Empty;
		_sendByDefault = (bool?)node["sendByDefault"] ?? false;
		_type = string.Equals((string?)node["type"], "terminalCommand", StringComparison.OrdinalIgnoreCase)
			? PromptActionType.TerminalCommand : PromptActionType.Prompt;
	}
	/// <inheritdoc />
	public override JsonObject Node => _node;
	/// <summary>Delivery modes offered in the picker.</summary>
	public static IReadOnlyList<PromptTemplateTypeOption> TypeOptions => PromptTemplateTypeOption.All;
	/// <summary>Delivery mode; legacy values normalize on assignment.</summary>
	public PromptActionType Type { get => _type; set { var normalized = PromptActionPolicy.Normalize(value); if (SetField(ref _type, normalized)) { RaiseChanged(); } } }
	/// <summary>Whether the body consumes the terminal selection, making this a selection action.</summary>
	public bool UsesSelectedText => Body.Contains("{selectedText}", StringComparison.Ordinal);
	/// <summary>Stable key surviving edits to the name or body.</summary>
	public string Id { get => _id; set { if (SetField(ref _id, value)) { OnPropertyChanged(nameof(TabHeader)); RaiseChanged(); } } }
	/// <summary>Label shown on the quick-action button.</summary>
	public string Name { get => _name; set { if (SetField(ref _name, value)) { OnPropertyChanged(nameof(TabHeader)); RaiseChanged(); } } }
	/// <summary>Template text, supporting placeholders such as <c>{selectedText}</c>.</summary>
	public string Body { get => _body; set { if (SetField(ref _body, value)) { OnPropertyChanged(nameof(UsesSelectedText)); RaiseChanged(); } } }
	/// <summary>
	/// Whether automated delivery also submits. Manual insertion never auto-submits regardless.
	/// </summary>
	public bool SendByDefault { get => _sendByDefault; set { if (SetField(ref _sendByDefault, value)) { RaiseChanged(); } } }
	/// <inheritdoc />
	public override string TabHeader { get { var name = !string.IsNullOrWhiteSpace(Name) ? Name : !string.IsNullOrWhiteSpace(Id) ? Id : "(new template)"; return IsItemDirty ? $"{name} •" : name; } }
	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["name"] = Name;
		_node["body"] = Body;
		_node["sendByDefault"] = SendByDefault;
		_node["type"] = Type == PromptActionType.TerminalCommand ? "terminalCommand" : "prompt";
	}
}
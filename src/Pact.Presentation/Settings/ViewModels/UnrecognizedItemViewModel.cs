using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Placeholder item for an array entry that a section's <c>TryCreateItem</c> could not
/// map to a known shape. The underlying node is kept and round-tripped verbatim.
/// </summary>
/// <param name="node">The JSON node preserved unchanged.</param>
public sealed class UnrecognizedItemViewModel(JsonObject node) : SettingsItemViewModelBase
{
	/// <inheritdoc />
	public override JsonObject Node { get; } = node;

	/// <inheritdoc />
	/// <remarks>
	/// Always <see langword="false"/>: the form cannot edit this entry, so it is shown read-only
	/// and written back exactly as it was read.
	/// </remarks>
	public override bool IsRecognized => false;

	/// <inheritdoc />
	public override string TabHeader => "Unrecognized entry";
}
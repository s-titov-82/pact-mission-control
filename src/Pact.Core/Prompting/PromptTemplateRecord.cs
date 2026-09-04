namespace Pact.Core.Prompting;

/// <summary>
/// One entry from <c>prompt-templates.json</c>: reusable text sent to an agent or shell.
/// </summary>
/// <param name="Id">Stable key surviving edits to the name or body.</param>
/// <param name="Name">Label shown on the quick-action button or in the template menu.</param>
/// <param name="Body">
/// Template text. Placeholders in <c>{name}</c> form are substituted by
/// <see cref="PromptTemplateRenderer"/>; unknown placeholders are left verbatim.
/// </param>
/// <param name="SendByDefault">
/// Whether the rendered text is submitted automatically. This governs only automated
/// delivery: text the user inserts manually is never auto-submitted.
/// </param>
/// <param name="Type">
/// Delivery mode, or <see langword="null"/> for templates written before the field existed.
/// Read <see cref="EffectiveType"/> rather than this raw value.
/// </param>
public sealed record PromptTemplateRecord(
	string Id,
	string Name,
	string Body,
	bool SendByDefault,
	PromptActionType? Type = null)
{
	/// <summary>
	/// Delivery mode with legacy and missing values resolved. Always prefer this over
	/// <see cref="Type"/> when deciding how to deliver the template.
	/// </summary>
	public PromptActionType EffectiveType => PromptActionPolicy.Normalize(Type);

	/// <summary>
	/// Whether the body consumes the current terminal selection, which decides if this
	/// template is offered as a selection action rather than a plain quick action.
	/// </summary>
	public bool UsesSelectedText =>
		Body?.Contains("{selectedText}", StringComparison.Ordinal) == true;
}
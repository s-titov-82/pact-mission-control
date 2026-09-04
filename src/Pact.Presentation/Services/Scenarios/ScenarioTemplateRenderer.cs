namespace Pact.Presentation.Services.Scenarios;

/// <summary>
/// Substitutes <c>{name}</c> placeholders in scenario prompt templates.
/// </summary>
public static class ScenarioTemplateRenderer
{
	/// <summary>
	/// Replaces each supplied placeholder in <paramref name="template"/> with its value.
	/// </summary>
	/// <returns>
	/// The rendered text. Placeholders with no supplied value are left verbatim, so a template
	/// referencing an unknown name stays visibly unfilled instead of silently losing text.
	/// The result is not the final prompt: the scenario engine appends its own protocol blocks.
	/// </returns>
	public static string Render(
		string template,
		IReadOnlyDictionary<string, string> values)
	{
		ArgumentNullException.ThrowIfNull(template);
		ArgumentNullException.ThrowIfNull(values);

		var rendered = template;
		foreach (var value in values)
		{
			rendered = rendered.Replace(
				"{" + value.Key + "}",
				value.Value,
				StringComparison.Ordinal);
		}

		return rendered;
	}
}
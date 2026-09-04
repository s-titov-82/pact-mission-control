using System.Text.RegularExpressions;

namespace Pact.Core.Prompting;

/// <summary>
/// Substitutes <c>{name}</c> placeholders in prompt template bodies.
/// </summary>
public sealed partial class PromptTemplateRenderer
{
	/// <summary>
	/// Replaces every recognized placeholder in <paramref name="template"/> with its value.
	/// </summary>
	/// <param name="template">Template body; placeholder names are case-sensitive.</param>
	/// <param name="variables">Values by placeholder name.</param>
	/// <returns>
	/// The rendered text. Placeholders absent from <paramref name="variables"/> are left
	/// verbatim rather than blanked, so an unfilled template stays visibly incomplete instead
	/// of silently losing content.
	/// </returns>
	// Kept an instance method despite CA1822: the renderer is passed as a constructor
	// dependency (SelectionActionRouter and friends), and making it static would leave
	// those injected parameters dead while changing every call site.
#pragma warning disable CA1822
	public string Render(string template, IReadOnlyDictionary<string, string> variables)
#pragma warning restore CA1822
	{
		ArgumentNullException.ThrowIfNull(template);
		ArgumentNullException.ThrowIfNull(variables);

		return VariablePattern().Replace(template, match =>
		{
			var key = match.Groups["name"].Value;
			return variables.TryGetValue(key, out var value) ? value : match.Value;
		});
	}

	[GeneratedRegex("\\{(?<name>[A-Za-z][A-Za-z0-9_]*)\\}")]
	private static partial Regex VariablePattern();
}
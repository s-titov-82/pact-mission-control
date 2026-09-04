using System.Text.RegularExpressions;
using Pact.Core.ScreenVerdictProfiles;

namespace Pact.Core.Sessions;

/// <summary>
/// Recognizes an interactive PowerShell prompt on the last non-empty screen line.
/// </summary>
public sealed partial class PwshScreenProfile : IAgentScreenProfile
{
	/// <summary>
	/// Gets the stateless PowerShell screen classifier.
	/// </summary>
	public static readonly PwshScreenProfile Instance = new();

	private PwshScreenProfile()
	{
	}

	/// <inheritdoc />
	public TerminalScreenVerdict Classify(string screen)
	{
		ArgumentNullException.ThrowIfNull(screen);

		var lastLine = screen
			.Split('\n')
			.LastOrDefault(line => line.Trim().Length > 0);
		return lastLine is not null && PromptPattern().IsMatch(lastLine)
			? new(TerminalScreenVerdictState.Done)
			: new(TerminalScreenVerdictState.Unknown);
	}

	[GeneratedRegex(@"^PS\s.*>\s*$")]
	private static partial Regex PromptPattern();
}
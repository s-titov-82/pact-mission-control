using System.Text.Json;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Reports a Claude launch settings argument that cannot be merged safely.</summary>
public sealed class ClaudeSettingsArgumentException(string message) : InvalidOperationException(message);

internal static class ClaudeSettingsArgument
{
	internal static void Edit(List<string> arguments, Func<string, string> update)
	{
		const string flag = "--settings";
		var index = arguments.FindLastIndex(argument => argument == flag || argument.StartsWith(flag + "=", StringComparison.Ordinal));
		var inline = index >= 0 && arguments[index] != flag;
		var valueIndex = inline ? index : index + 1;
		if (index >= 0 && valueIndex >= arguments.Count)
		{
			throw new ClaudeSettingsArgumentException("The existing Claude settings argument (--settings) has no value.");
		}
		var original = index < 0 ? "{}" : inline ? arguments[valueIndex][(flag.Length + 1)..] : arguments[valueIndex];
		if (!original.TrimStart().StartsWith('{'))
		{
			throw new ClaudeSettingsArgumentException("The existing Claude settings argument (--settings) is not an inline JSON object.");
		}
		string updated;
		try
		{
			updated = update(original);
		}
		catch (JsonException)
		{
			throw new ClaudeSettingsArgumentException("The existing Claude settings argument (--settings) contains invalid JSON.");
		}
		if (index < 0)
		{
			arguments.Add(flag);
			arguments.Add(updated);
		}
		else
		{
			arguments[valueIndex] = (inline ? flag + "=" : "") + updated;
		}
	}
}

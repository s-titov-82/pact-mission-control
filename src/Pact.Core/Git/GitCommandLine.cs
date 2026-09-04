namespace Pact.Core.Git;

/// <summary>
/// Splits a user-edited git command string (from git popup settings) into process arguments.
/// Double quotes group whitespace into one argument, backslash escapes a quote inside quotes,
/// and a leading "git" token is tolerated and stripped so both "pull" and "git pull" work.
/// </summary>
public static class GitCommandLine
{
	/// <summary>
	/// Splits a command string into arguments.
	/// </summary>
	/// <returns>The arguments, or an empty list when the command is null or blank.</returns>
	/// <exception cref="FormatException">
	/// The command is malformed, most commonly an unterminated quote. Use
	/// <see cref="TrySplit"/> when validating text the user is still editing.
	/// </exception>
	public static IReadOnlyList<string> Split(string? command)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return [];
		}

		List<string> tokens = [];
		System.Text.StringBuilder current = new();
		var inQuotes = false;
		var tokenStarted = false;

		for (var index = 0; index < command.Length; index++)
		{
			var character = command[index];

			if (inQuotes && character == '\\' && index + 1 < command.Length && command[index + 1] == '"')
			{
				current.Append('"');
				index++;
				continue;
			}

			if (character == '"')
			{
				inQuotes = !inQuotes;
				tokenStarted = true;
				continue;
			}

			if (!inQuotes && char.IsWhiteSpace(character))
			{
				if (tokenStarted)
				{
					tokens.Add(current.ToString());
					current.Clear();
					tokenStarted = false;
				}

				continue;
			}

			current.Append(character);
			tokenStarted = true;
		}

		if (inQuotes)
		{
			throw new FormatException($"Unbalanced quote in git command: {command}");
		}

		if (tokenStarted)
		{
			tokens.Add(current.ToString());
		}

		if (tokens.Count > 0 && string.Equals(tokens[0], "git", StringComparison.OrdinalIgnoreCase))
		{
			tokens.RemoveAt(0);
		}

		return tokens;
	}

	/// <summary>
	/// Splits a command string without throwing on malformed input, for validating text as the
	/// user types it.
	/// </summary>
	/// <param name="command">Command string to split.</param>
	/// <param name="arguments">The arguments, or an empty list when the command is malformed.</param>
	/// <returns><see langword="false"/> when the command could not be parsed.</returns>
	public static bool TrySplit(string? command, out IReadOnlyList<string> arguments)
	{
		try
		{
			arguments = Split(command);
			return true;
		}
		catch (FormatException)
		{
			arguments = [];
			return false;
		}
	}
}
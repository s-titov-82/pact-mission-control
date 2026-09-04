namespace Pact.Core.Git;

/// <summary>
/// Parses <c>git status --porcelain=v2 --branch</c> output into a
/// <see cref="GitStatusSnapshot"/>.
/// </summary>
public static class GitStatusParser
{
	private const string BranchHeadPrefix = "# branch.head ";
	private const string BranchUpstreamPrefix = "# branch.upstream ";
	private const string BranchAheadBehindPrefix = "# branch.ab ";

	/// <summary>
	/// Parses porcelain v2 output. Accepts both LF and CRLF line endings.
	/// </summary>
	/// <returns>
	/// The parsed snapshot. Lines that are unrecognized or malformed are skipped rather than
	/// throwing, so a newer git emitting extra headers degrades to a partial snapshot instead
	/// of breaking the git panel.
	/// </returns>
	public static GitStatusSnapshot Parse(string porcelainV2Output)
	{
		ArgumentNullException.ThrowIfNull(porcelainV2Output);

		var branch = string.Empty;
		string? upstream = null;
		var ahead = 0;
		var behind = 0;
		var isDetached = false;
		List<GitFileEntry> files = [];

		foreach (var rawLine in porcelainV2Output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			if (string.IsNullOrWhiteSpace(line))
			{
				continue;
			}

			if (line.StartsWith(BranchHeadPrefix, StringComparison.Ordinal))
			{
				branch = line[BranchHeadPrefix.Length..];
				isDetached = string.Equals(branch, "(detached)", StringComparison.Ordinal);
				continue;
			}

			if (line.StartsWith(BranchUpstreamPrefix, StringComparison.Ordinal))
			{
				upstream = line[BranchUpstreamPrefix.Length..];
				continue;
			}

			if (line.StartsWith(BranchAheadBehindPrefix, StringComparison.Ordinal))
			{
				(ahead, behind) = ParseAheadBehind(line[BranchAheadBehindPrefix.Length..]);
				continue;
			}

			if (line.StartsWith("1 ", StringComparison.Ordinal))
			{
				AddOrdinaryEntry(files, line);
				continue;
			}

			if (line.StartsWith("2 ", StringComparison.Ordinal))
			{
				AddRenamedEntry(files, line);
				continue;
			}

			if (line.StartsWith("? ", StringComparison.Ordinal))
			{
				var path = line[2..];
				if (!string.IsNullOrEmpty(path))
				{
					files.Add(new GitFileEntry(path, null, GitChangeKind.Untracked));
				}

				continue;
			}

			if (line.StartsWith("u ", StringComparison.Ordinal))
			{
				var path = GetRemainderAfterTokens(line, 10);
				if (!string.IsNullOrEmpty(path))
				{
					files.Add(new GitFileEntry(path, null, GitChangeKind.Conflicted));
				}
			}
		}

		return new GitStatusSnapshot(branch, upstream, ahead, behind, isDetached, files);
	}

	private static (int Ahead, int Behind) ParseAheadBehind(string value)
	{
		var ahead = 0;
		var behind = 0;

		foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
		{
			if (part.StartsWith('+'))
			{
				_ = int.TryParse(part[1..], out ahead);
			}
			else if (part.StartsWith('-'))
			{
				_ = int.TryParse(part[1..], out behind);
			}
		}

		return (ahead, behind);
	}

	private static void AddOrdinaryEntry(List<GitFileEntry> files, string line)
	{
		if (line.Length < 4)
		{
			return;
		}

		var kind = Classify(line.Substring(2, 2));
		var path = GetRemainderAfterTokens(line, 8);
		if (kind is null || string.IsNullOrEmpty(path))
		{
			return;
		}

		files.Add(new GitFileEntry(path, null, kind.Value));
	}

	private static void AddRenamedEntry(List<GitFileEntry> files, string line)
	{
		if (line.Length < 4)
		{
			return;
		}

		var kind = Classify(line.Substring(2, 2));
		var pathFields = GetRemainderAfterTokens(line, 9);
		if (kind is null || string.IsNullOrEmpty(pathFields))
		{
			return;
		}

		var paths = pathFields.Split('\t', 2);
		var path = paths[0];
		var originalPath = paths.Length > 1 ? paths[1] : null;

		if (!string.IsNullOrEmpty(path))
		{
			files.Add(new GitFileEntry(path, originalPath, kind.Value));
		}
	}

	private static GitChangeKind? Classify(string xy)
	{
		if (xy.Contains('U', StringComparison.Ordinal))
		{
			return GitChangeKind.Conflicted;
		}

		if (xy.Contains('A', StringComparison.Ordinal))
		{
			return GitChangeKind.Added;
		}

		if (xy.Contains('D', StringComparison.Ordinal))
		{
			return GitChangeKind.Deleted;
		}

		if (xy.Contains('M', StringComparison.Ordinal) ||
			xy.Contains('R', StringComparison.Ordinal) ||
			xy.Contains('T', StringComparison.Ordinal))
		{
			return GitChangeKind.Modified;
		}

		return null;
	}

	private static string? GetRemainderAfterTokens(string line, int tokenCount)
	{
		var index = 0;

		for (var token = 0; token < tokenCount; token++)
		{
			while (index < line.Length && line[index] == ' ')
			{
				index++;
			}

			var nextSpace = line.IndexOf(' ', index);
			if (nextSpace < 0)
			{
				return null;
			}

			index = nextSpace + 1;
		}

		while (index < line.Length && line[index] == ' ')
		{
			index++;
		}

		return index < line.Length ? line[index..] : null;
	}
}
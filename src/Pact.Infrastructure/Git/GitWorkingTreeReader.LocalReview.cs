using System.Globalization;
using Pact.Core.Git;
using Pact.Core.Remarks;

namespace Pact.Infrastructure.Git;

public sealed partial class GitWorkingTreeReader
{
	private Dictionary<(string Root, string? Base, string? Head), GitLocalChanges>? _batchLocalChanges;
	/// <inheritdoc />
	public IGitReviewReader CreateReadBatch() => new GitWorkingTreeReader(runner) { _batchLocalChanges = [] };

	/// <inheritdoc />
	public async Task<GitLocalChanges> ReadLocalChangesAsync(string root, string? baseCommit, CancellationToken cancellationToken)
	{
		if (baseCommit is not null && !IsCommitId(baseCommit))
		{ throw new ArgumentException("A full base id is required.", nameof(baseCommit)); }
		var head = await ResolveReviewIdAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);
		if (_batchLocalChanges?.TryGetValue((root, baseCommit, head), out var cached) == true)
		{ return cached; }
		var baseline = baseCommit ?? EmptyTreeId;
		var status = await RunAsync(root, ["status", "--porcelain=v2", "--branch", "--untracked-files=all"], cancellationToken).ConfigureAwait(false);
		if (!status.Succeeded)
		{ throw new IOException(status.StandardError.Trim()); }
		var names = await RunAsync(root, ["diff", "--name-status", "-z", "-M", "--no-ext-diff", "--no-textconv", baseline, "--"], cancellationToken).ConfigureAwait(false);
		var stat = await RunAsync(root, ["diff", "--numstat", "-z", "-M", "--no-ext-diff", "--no-textconv", baseline, "--"], cancellationToken).ConfigureAwait(false);
		if (!names.Succeeded || !stat.Succeeded)
		{ throw new IOException(names.StandardError + stat.StandardError); }
		var files = ParseCommitFiles(names.StandardOutput).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
		// An index-untracked path that exists in the base appears in native diff as a deletion
		// or rename source. Avoid a Git subprocess for every genuinely new untracked file.
		var baseCandidates = files.Values.SelectMany(entry => entry.OriginalPath is { } old
			? new[] { entry.Path, old } : [entry.Path]).ToHashSet(StringComparer.Ordinal);
		var totals = ParseLocalNumstat(stat.StandardOutput);
		var untracked = GitStatusParser.Parse(status.StandardOutput).Files.Where(file => file.Kind == GitChangeKind.Untracked).ToArray();
		var untrackedPaths = untracked.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
		// A rename's old name can have been recreated outside the index. In that case
		// the new name is an addition, while the restored old name is compared separately.
		foreach (var entry in files.Values.ToArray())
		{
			if (entry.OriginalPath is not { } old || !untrackedPaths.Contains(old))
			{ continue; }
			var added = entry with { OriginalPath = null, Kind = GitChangeKind.Added };
			files[entry.Path] = added;
			var diff = await ReadLocalDiffAsync(root, added, baseCommit, cancellationToken).ConfigureAwait(false);
			totals[entry.Path] = DiffTotals(diff);
		}
		foreach (var entry in untracked)
		{
			var exists = baseCommit is not null && baseCandidates.Contains(entry.Path)
				&& await BasePathExistsAsync(root, baseCommit, entry.Path, cancellationToken).ConfigureAwait(false);
			var diff = await ReadLocalDiffCoreAsync(root, entry, baseCommit, exists, cancellationToken).ConfigureAwait(false);
			if (diff.Placeholder is null && diff.Hunks.Count == 0)
			{
				files.Remove(entry.Path);
				totals.Remove(entry.Path);
				// Empty new files still belong to the review even though no lines changed.
				if (!exists)
				{ files[entry.Path] = entry; }
				continue;
			}
			files[entry.Path] = entry;
			totals[entry.Path] = DiffTotals(diff);
		}
		if (await ResolveReviewIdAsync(root, "HEAD", cancellationToken).ConfigureAwait(false) != head)
		{ throw new IOException("HEAD changed during the local comparison. Refresh to retry."); }
		GitLocalChanges result = new(baseCommit, head, files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray(),
			totals.Where(pair => files.ContainsKey(pair.Key)).Sum(pair => pair.Value.Added),
			totals.Where(pair => files.ContainsKey(pair.Key)).Sum(pair => pair.Value.Removed));
		_batchLocalChanges?.Add((root, baseCommit, head), result);
		return result;
	}

	/// <inheritdoc />
	public Task<FileDiff> ReadLocalDiffAsync(string root, GitFileEntry entry, string? baseCommit, CancellationToken cancellationToken) =>
		ReadLocalDiffCoreAsync(root, entry, baseCommit, null, cancellationToken);

	private async Task<FileDiff> ReadLocalDiffCoreAsync(string root, GitFileEntry entry, string? baseCommit, bool? basePathExists, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		if (baseCommit is not null && !IsCommitId(baseCommit))
		{ throw new ArgumentException("A full base id is required.", nameof(baseCommit)); }
		if (entry.Kind == GitChangeKind.Untracked)
		{
			var (after, placeholder) = await ReadTextAsync(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false);
			if (after is null)
			{ return placeholder!; }
			if (baseCommit is null || !(basePathExists ?? await BasePathExistsAsync(root, baseCommit, entry.Path, cancellationToken).ConfigureAwait(false)))
			{ return FileDiff.AllAdded(after); }
			var (before, oldPlaceholder) = await ReadCommitBlobAsync(root, entry.Path, baseCommit, cancellationToken).ConfigureAwait(false);
			return before is null ? oldPlaceholder ?? new FileDiff("Base content is unavailable", []) : CompareLocalText(before, after);
		}
		if (entry.Kind != GitChangeKind.Deleted)
		{
			var (_, placeholder) = await ReadTextAsync(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false);
			if (placeholder is not null)
			{ return placeholder; }
		}
		if (baseCommit is not null && entry.Kind != GitChangeKind.Added)
		{
			var (_, placeholder) = await ReadCommitBlobAsync(root, entry.OriginalPath ?? entry.Path, baseCommit, cancellationToken).ConfigureAwait(false);
			if (placeholder is not null)
			{ return placeholder; }
		}
		List<string> args = ["diff", "--no-color", "--no-ext-diff", "--no-textconv", "-M", baseCommit ?? EmptyTreeId, "--"];
		if (entry.OriginalPath is { } original)
		{ args.Add(":(literal)" + original); }
		args.Add(":(literal)" + entry.Path);
		var result = await RunAsync(root, args, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{ throw new IOException(result.StandardError.Trim()); }
		return UnifiedDiffParser.Parse(result.StandardOutput);
	}

	private async Task<bool> BasePathExistsAsync(string root, string baseline, string path, CancellationToken cancellationToken) =>
		(await RunAsync(root, ["cat-file", "-e", baseline + ":" + path], cancellationToken).ConfigureAwait(false)).Succeeded;

	private static (int Added, int Removed) DiffTotals(FileDiff diff) => (
		diff.Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Added)),
		diff.Hunks.Sum(hunk => hunk.Lines.Count(line => line.Kind == DiffLineKind.Removed)));

	private static Dictionary<string, (int Added, int Removed)> ParseLocalNumstat(string output)
	{
		Dictionary<string, (int, int)> result = new(StringComparer.Ordinal);
		var fields = output.Split('\0');
		for (var index = 0; index < fields.Length - 1; index++)
		{
			var parts = fields[index].Split('\t', 3);
			if (parts.Length != 3)
			{ throw new InvalidDataException("Invalid local numstat."); }
			var path = parts[2];
			if (path.Length == 0)
			{
				if (index + 2 >= fields.Length - 1)
				{ throw new InvalidDataException("Incomplete rename numstat."); }
				path = fields[index + 2];
				index += 2;
			}
			result[path] = (int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var added) ? added : 0,
				int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var removed) ? removed : 0);
		}
		return result;
	}

	// Only for untracked paths present in the captured base: Git's index-aware
	// diff sees them as deleted. Reuse the bounded line mapper without staging them.
	private static FileDiff CompareLocalText(string before, string after)
	{
		if (before == after)
		{ return new(null, []); }
		var oldLines = LocalDiffLines(before);
		var newLines = LocalDiffLines(after);
		var map = LineDiff.MapUnchanged(oldLines, newLines);
		if (map is null)
		{ return new("Local replacement exceeds the safe diff edit budget", []); }
		if (before.EndsWith('\n') != after.EndsWith('\n') && oldLines.Length > 0 && newLines.Length > 0
			&& map[^1] == newLines.Length - 1)
		{ map[^1] = null; }
		List<DiffLine> lines = [];
		var next = 0;
		for (var old = 0; old < oldLines.Length; old++)
		{
			if (map[old] is not { } match)
			{ lines.Add(new(DiffLineKind.Removed, old + 1, null, oldLines[old].TrimEnd('\r'))); continue; }
			while (next < match)
			{ lines.Add(new(DiffLineKind.Added, null, next + 1, newLines[next++].TrimEnd('\r'))); }
			lines.Add(new(DiffLineKind.Context, old + 1, next + 1, oldLines[old].TrimEnd('\r')));
			next++;
		}
		while (next < newLines.Length)
		{ lines.Add(new(DiffLineKind.Added, null, next + 1, newLines[next++].TrimEnd('\r'))); }
		return lines.All(line => line.Kind == DiffLineKind.Context) ? new(null, [])
			: new(null, [new(oldLines.Length > 0 ? 1 : 0, oldLines.Length, newLines.Length > 0 ? 1 : 0, newLines.Length, string.Empty, lines)]);
	}
	private static string[] LocalDiffLines(string text)
	{
		if (text.Length == 0)
		{ return []; }
		var lines = text.Split('\n');
		return lines[^1].Length == 0 ? lines[..^1] : lines;
	}
}
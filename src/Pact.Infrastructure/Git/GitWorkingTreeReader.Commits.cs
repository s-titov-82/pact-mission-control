using System.Globalization;
using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

public sealed partial class GitWorkingTreeReader
{
	private const string CommitFormat = "%H%x00%P%x00%an%x00%aI%x00%s%x00";

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(skip);
		ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 50);
		var result = await RunAsync(root, ["log", "--date-order", $"--skip={skip.ToString(CultureInfo.InvariantCulture)}",
			$"--max-count={count.ToString(CultureInfo.InvariantCulture)}", $"--format={CommitFormat}", "HEAD", "--"], cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{
			var repository = await RunAsync(root, ["rev-parse", "--git-dir"], cancellationToken).ConfigureAwait(false);
			if (repository.Succeeded && await ResolveCommitAsync(root, "HEAD", cancellationToken).ConfigureAwait(false) is null)
			{ return []; }
			throw new IOException(result.StandardError.Trim());
		}
		return ParseCommits(result.StandardOutput);
	}
	/// <inheritdoc />
	public async Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(revision);
		var resolved = await RunAsync(root, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], cancellationToken).ConfigureAwait(false);
		var id = resolved.StandardOutput.Trim();
		if (!resolved.Succeeded || !IsCommitId(id))
		{ return null; }
		var metadata = await RunAsync(root, ["show", "--no-patch", $"--format={CommitFormat}", id, "--"], cancellationToken).ConfigureAwait(false);
		if (!metadata.Succeeded)
		{ return null; }
		var commits = ParseCommits(metadata.StandardOutput);
		return commits.Count == 1 ? commits[0] : null;
	}
	/// <inheritdoc />
	public async Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken)
	{
		ValidateCommit(commit);
		var changes = await ReadComparisonChangesAsync(root, commit.BaseCommit, commit.Id, cancellationToken).ConfigureAwait(false);
		return new(commit, changes.Files, changes.AddedLines, changes.RemovedLines);
	}
	/// <inheritdoc />
	public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken)
	{
		ValidateCommit(commit);
		return ReadComparisonDiffAsync(root, entry, commit.BaseCommit, commit.Id, cancellationToken);
	}
	/// <inheritdoc />
	public async Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken)
	{
		var (text, _) = await ReadCommitBlobAsync(root, path, commitId, cancellationToken).ConfigureAwait(false);
		return text;
	}

	private async Task<(string? Text, FileDiff? Placeholder)> ReadCommitBlobAsync(string root, string path, string commitId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!IsCommitId(commitId))
		{ throw new ArgumentException("A full commit id is required.", nameof(commitId)); }
		var spec = commitId + ":" + path;
		var size = await RunAsync(root, ["cat-file", "-s", spec], cancellationToken).ConfigureAwait(false);
		if (!size.Succeeded || !long.TryParse(size.StandardOutput.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length))
		{ return (null, new FileDiff("File content is unavailable", [])); }
		if (length > MaxFileBytes)
		{ return (null, FileDiff.TooLarge); }
		var content = await RunAsync(root, ["cat-file", "blob", spec], cancellationToken).ConfigureAwait(false);
		// Gitlinks have commit rather than blob content; their textual diff is still useful.
		if (!content.Succeeded)
		{ return (null, null); }
		var text = content.StandardOutput;
		if (text.AsSpan(0, Math.Min(text.Length, 8000)).Contains('\0'))
		{ return (null, FileDiff.Binary); }
		return (text.TrimStart('\uFEFF'), null);
	}

	private static List<string> CommitDiffArguments(string? baseCommit, string targetCommit, params string[] options)
	{
		List<string> args = baseCommit is null ? ["diff-tree", "--root", "--no-commit-id", "-r"] : ["diff"];
		args.AddRange(options);
		if (baseCommit is not null)
		{ args.Add(baseCommit); }
		args.Add(targetCommit);
		args.Add("--");
		return args;
	}

	private static List<GitCommitInfo> ParseCommits(string text)
	{
		var fields = text.Split('\0');
		List<GitCommitInfo> commits = [];
		for (var index = 0; index + 4 < fields.Length; index += 5)
		{
			var id = fields[index].Trim();
			var parents = fields[index + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (!IsCommitId(id) || parents.Any(parent => !IsCommitId(parent))
				|| !DateTimeOffset.TryParse(fields[index + 3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
			{ throw new InvalidDataException("Git returned invalid commit metadata."); }
			commits.Add(new(id, parents, fields[index + 2], at, fields[index + 4]));
		}
		return commits;
	}

	private static List<GitFileEntry> ParseCommitFiles(string text)
	{
		var fields = text.Split('\0');
		List<GitFileEntry> files = [];
		for (var index = 0; index < fields.Length - 1;)
		{
			var status = fields[index++];
			if (status.Length == 0 || index >= fields.Length - 1)
			{ throw new InvalidDataException("Git returned invalid changed paths."); }
			var path = fields[index++];
			string? original = null;
			if (status[0] is 'R' or 'C')
			{
				original = path;
				if (index >= fields.Length - 1)
				{ throw new InvalidDataException("Git returned an incomplete rename."); }
				path = fields[index++];
			}
			files.Add(new(path, original, status[0] switch { 'A' => GitChangeKind.Added, 'D' => GitChangeKind.Deleted, _ => GitChangeKind.Modified }));
		}
		return files;
	}

	private static bool IsCommitId(string id) => id.Length is 40 or 64 && id.All(char.IsAsciiHexDigit);
	private static void ValidateCommit(GitCommitInfo commit)
	{
		ArgumentNullException.ThrowIfNull(commit);
		if (!IsCommitId(commit.Id) || commit.Parents.Any(parent => !IsCommitId(parent)))
		{ throw new ArgumentException("Full resolved commit ids are required.", nameof(commit)); }
	}
}
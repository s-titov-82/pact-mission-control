using System.Globalization;
using Pact.Core.Git;
using Pact.Infrastructure.Files;

namespace Pact.Infrastructure.Git;

/// <summary>Uncommitted changes of a repository compared with HEAD.</summary>
/// <param name="HeadCommit">The HEAD commit id, or <see langword="null"/> while HEAD is unborn.</param>
/// <param name="Branch">Current branch name, as git status reports it.</param>
/// <param name="Files">Changed paths, untracked files listed one by one.</param>
/// <param name="AddedLines">Lines added in tracked files; untracked files are not counted.</param>
/// <param name="RemovedLines">Lines removed in tracked files.</param>
public sealed record WorkingTreeChanges(
	string? HeadCommit,
	string Branch,
	IReadOnlyList<GitFileEntry> Files,
	int AddedLines,
	int RemovedLines);

/// <summary>Reads a repository's uncommitted changes and per-file diffs without taking git locks.</summary>
public interface IGitWorkingTreeReader
{
	/// <summary>Reads the changed paths and line totals.</summary>
	/// <returns><see langword="null"/> when git status fails, for instance outside a repository.</returns>
	Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken);

	/// <summary>Reads one changed path's diff against <paramref name="headCommit"/>, or the empty tree when it is null.</summary>
	Task<FileDiff> ReadDiffAsync(
		string root,
		GitFileEntry entry,
		string? headCommit,
		CancellationToken cancellationToken);

	/// <summary>
	/// Reads a working-tree file as text; null when it is missing, larger than
	/// <see cref="GitWorkingTreeReader.MaxFileBytes"/>, or binary.
	/// </summary>
	Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IGitWorkingTreeReader"/>. Every call passes <c>--no-optional-locks</c> so an
/// agent's own git commands never meet a lock Pact holds, and <c>core.quotePath=false</c> so
/// non-ASCII paths come back verbatim.
/// </summary>
public sealed class GitWorkingTreeReader(IGitCliRunner runner) : IGitWorkingTreeReader
{
	/// <summary>The id of git's empty tree, the comparison base while HEAD is unborn.</summary>
	public const string EmptyTreeId = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

	/// <summary>Untracked files larger than this are not read for display.</summary>
	public const long MaxFileBytes = TextFileReader.MaxBytes;

	/// <inheritdoc />
	public async Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken)
	{
		var status = await RunAsync(
			root,
			["status", "--porcelain=v2", "--branch", "--untracked-files=all"],
			cancellationToken).ConfigureAwait(false);
		if (!status.Succeeded)
		{
			return null;
		}

		var snapshot = GitStatusParser.Parse(status.StandardOutput);
		var head = await RunAsync(root, ["rev-parse", "--verify", "-q", "HEAD"], cancellationToken).ConfigureAwait(false);
		var headCommit = head.Succeeded ? head.StandardOutput.Trim() : null;
		var numstat = await RunAsync(
			root,
			["diff", "--numstat", headCommit ?? EmptyTreeId],
			cancellationToken).ConfigureAwait(false);
		var (added, removed) = numstat.Succeeded ? SumNumstat(numstat.StandardOutput) : (0, 0);
		return new WorkingTreeChanges(headCommit, snapshot.Branch, snapshot.Files, added, removed);
	}

	/// <inheritdoc />
	public async Task<FileDiff> ReadDiffAsync(
		string root,
		GitFileEntry entry,
		string? headCommit,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		if (entry.Kind == GitChangeKind.Untracked)
		{
			return await ReadUntrackedAsync(
				Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)),
				cancellationToken).ConfigureAwait(false);
		}

		List<string> arguments = ["diff", "--no-color", "--no-ext-diff", "-M", headCommit ?? EmptyTreeId, "--"];
		if (entry.OriginalPath is { } originalPath)
		{
			arguments.Add(originalPath);
		}

		arguments.Add(entry.Path);
		var diff = await RunAsync(root, arguments, cancellationToken).ConfigureAwait(false);
		return diff.Succeeded ? UnifiedDiffParser.Parse(diff.StandardOutput) : new FileDiff(null, []);
	}

	private Task<GitCommandResult> RunAsync(
		string root,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken) =>
		runner.RunAsync(
			root,
			["-c", "core.quotePath=false", "--no-optional-locks", .. arguments],
			outputLine: null,
			cancellationToken);

	/// <inheritdoc />
	public async Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		try
		{
			var (text, _) = await ReadTextAsync(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken)
				.ConfigureAwait(false);
			return text;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static async Task<FileDiff> ReadUntrackedAsync(string path, CancellationToken cancellationToken)
	{
		var (text, placeholder) = await ReadTextAsync(path, cancellationToken).ConfigureAwait(false);
		return text is null ? placeholder! : FileDiff.AllAdded(text);
	}

	// The text of a file, or why there is none: missing (an empty diff), too large or binary.
	private static async Task<(string? Text, FileDiff? Placeholder)> ReadTextAsync(string path, CancellationToken cancellationToken)
	{
		var content = await TextFileReader.ReadAsync(path, cancellationToken).ConfigureAwait(false);
		return content.State switch
		{
			TextFileState.Text => (content.Text, null),
			TextFileState.TooLarge => (null, FileDiff.TooLarge),
			TextFileState.Binary => (null, FileDiff.Binary),
			_ => (null, new FileDiff(null, []))
		};
	}

	private static (int Added, int Removed) SumNumstat(string output)
	{
		var added = 0;
		var removed = 0;
		foreach (var line in output.Split('\n'))
		{
			var fields = line.Split('\t');
			if (fields.Length < 3)
			{
				continue;
			}

			// Binary files report "-" for both counts.
			if (int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var plus))
			{
				added += plus;
			}

			if (int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minus))
			{
				removed += minus;
			}
		}

		return (added, removed);
	}
}

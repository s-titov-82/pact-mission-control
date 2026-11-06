using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

public sealed partial class GitWorkingTreeReader
{
	/// <inheritdoc />
	public async Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken)
	{
		ValidateComparison(baseCommit, targetCommit);
		var status = await RunAsync(root, CommitDiffArguments(baseCommit, targetCommit, "--name-status", "-z", "-M"), cancellationToken).ConfigureAwait(false);
		if (!status.Succeeded)
		{ throw new IOException(status.StandardError.Trim()); }
		var numstat = await RunAsync(root, CommitDiffArguments(baseCommit, targetCommit, "--numstat", "--no-ext-diff", "--no-textconv"), cancellationToken).ConfigureAwait(false);
		if (!numstat.Succeeded)
		{ throw new IOException(numstat.StandardError.Trim()); }
		var (added, removed) = SumNumstat(numstat.StandardOutput);
		return new(baseCommit, targetCommit, ParseCommitFiles(status.StandardOutput), added, removed);
	}
	/// <inheritdoc />
	public async Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(entry);
		ValidateComparison(baseCommit, targetCommit);
		if (entry.Kind != GitChangeKind.Deleted)
		{
			var (_, placeholder) = await ReadCommitBlobAsync(root, entry.Path, targetCommit, cancellationToken).ConfigureAwait(false);
			if (placeholder is not null)
			{ return placeholder; }
		}
		if (baseCommit is not null && entry.Kind != GitChangeKind.Added)
		{
			var (_, placeholder) = await ReadCommitBlobAsync(root, entry.OriginalPath ?? entry.Path, baseCommit, cancellationToken).ConfigureAwait(false);
			if (placeholder is not null)
			{ return placeholder; }
		}
		var args = CommitDiffArguments(baseCommit, targetCommit, "-p", "--no-color", "--no-ext-diff", "--no-textconv", "-M");
		if (entry.OriginalPath is { } original)
		{ args.Add(":(literal)" + original); }
		args.Add(":(literal)" + entry.Path);
		var result = await RunAsync(root, args, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{ throw new IOException(result.StandardError.Trim()); }
		return UnifiedDiffParser.Parse(result.StandardOutput);
	}
	private static void ValidateComparison(string? baseCommit, string targetCommit)
	{
		if (!IsCommitId(targetCommit) || (baseCommit is not null && !IsCommitId(baseCommit)))
		{ throw new ArgumentException("Full resolved commit ids are required.", nameof(targetCommit)); }
	}
}

using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

public sealed partial class GitWorkingTreeReader
{
	/// <inheritdoc />
	public async Task<GitBranchChanges> ReadBranchChangesAsync(string root, CancellationToken cancellationToken)
	{
		var status = await RunAsync(root, ["status", "--porcelain=v2", "--branch", "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
		if (!status.Succeeded)
		{ return new(string.Empty, null, null, "Repository unavailable."); }
		var branch = GitStatusParser.Parse(status.StandardOutput);
		if (branch.IsDetached)
		{ return new(branch.Branch, null, null, "Detached HEAD has no tracking upstream."); }
		var head = await RunAsync(root, ["rev-parse", "--verify", "--end-of-options", "HEAD^{commit}"], cancellationToken).ConfigureAwait(false);
		if (!head.Succeeded)
		{ return new(branch.Branch, branch.Upstream, null, "HEAD is unborn."); }
		if (branch.Upstream is null)
		{ return new(branch.Branch, null, null, "No tracking upstream is configured."); }
		var trackingRevision = branch.Branch + "@{upstream}";
		var upstream = await RunAsync(root, ["rev-parse", "--verify", "--end-of-options", trackingRevision + "^{commit}"], cancellationToken).ConfigureAwait(false);
		if (!upstream.Succeeded)
		{ return new(branch.Branch, branch.Upstream, null, "Configured upstream is unavailable."); }
		var target = head.StandardOutput.Trim();
		var baseline = upstream.StandardOutput.Trim();
		var comparison = await ReadComparisonChangesAsync(root, baseline, target, cancellationToken).ConfigureAwait(false);
		var currentBranch = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken).ConfigureAwait(false);
		var confirmed = await RunAsync(root, ["rev-parse", "--revs-only", "--end-of-options", "HEAD", trackingRevision], cancellationToken).ConfigureAwait(false);
		var ids = confirmed.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
		if (!currentBranch.Succeeded || currentBranch.StandardOutput.Trim() != branch.Branch || !confirmed.Succeeded
			|| ids.Length != 2 || ids[0] != target || ids[1] != baseline)
		{ throw new IOException("The branch or upstream changed while reading its comparison. Refresh to retry."); }
		return new(branch.Branch, branch.Upstream, comparison, null);
	}
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

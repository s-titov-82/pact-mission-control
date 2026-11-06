using System.Globalization;

namespace Pact.Infrastructure.Git;

public sealed partial class GitWorkingTreeReader
{
	/// <inheritdoc />
	public async Task<IReadOnlyList<string>> ReadBranchesAsync(string root, CancellationToken cancellationToken)
	{
		var result = await RunAsync(root, ["for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes"], cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{ throw new IOException(result.StandardError.Trim()); }
		return result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
			.Where(name => !name.EndsWith("/HEAD", StringComparison.Ordinal)).ToArray();
	}

	/// <inheritdoc />
	public async Task<GitReviewChanges> ReadReviewChangesAsync(string root, GitReviewScope scope, string? targetBranch, CancellationToken cancellationToken)
	{
		if (!Enum.IsDefined(scope))
		{ throw new ArgumentOutOfRangeException(nameof(scope)); }
		var status = await RunAsync(root, ["status", "--porcelain=v2", "--branch", "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
		if (!status.Succeeded)
		{ return new(scope, string.Empty, null, null, 0, 0, false, "Repository unavailable."); }
		var branch = Pact.Core.Git.GitStatusParser.Parse(status.StandardOutput);
		GitReviewChanges Unavailable(string reason)
		{ return new(scope, branch.Branch, targetBranch, null, branch.Ahead, branch.Behind, false, reason); }
		var head = await ResolveReviewIdAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);
		string? baseline = head;
		string? baseRef = "HEAD";
		string? refId = head;
		var fallback = scope == GitReviewScope.Unpushed && branch.Upstream is null;
		if (scope != GitReviewScope.Uncommitted)
		{
			if (branch.IsDetached)
			{ return Unavailable("Detached HEAD: choose Uncommitted or an individual commit."); }
			if (head is null)
			{ return Unavailable("HEAD is unborn: choose Uncommitted."); }
			if (scope == GitReviewScope.Unpushed && !fallback)
			{
				baseRef = branch.Branch + "@{upstream}";
				baseline = refId = await ResolveReviewIdAsync(root, baseRef, cancellationToken).ConfigureAwait(false);
				if (baseline is null)
				{ return Unavailable("Configured tracking ref is unavailable."); }
			}
			else
			{
				baseRef = targetBranch ?? await DefaultReviewTargetAsync(root, branch.Branch, cancellationToken).ConfigureAwait(false);
				if (baseRef is null)
				{ return Unavailable("Select a target branch."); }
				refId = await ResolveReviewIdAsync(root, baseRef, cancellationToken).ConfigureAwait(false);
				if (refId is null)
				{ return Unavailable("Selected target branch is unavailable."); }
				var merge = await RunAsync(root, ["merge-base", "--all", head, refId], cancellationToken).ConfigureAwait(false);
				var bases = merge.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
				if (!merge.Succeeded || bases.Length == 0)
				{ return Unavailable("No common ancestor with the target branch."); }
				if (bases.Length != 1)
				{ return Unavailable("Multiple merge bases: select another target branch."); }
				baseline = bases[0];
			}
		}
		var changes = await ReadLocalChangesAsync(root, baseline, cancellationToken).ConfigureAwait(false);
		var currentHead = await ResolveReviewIdAsync(root, "HEAD", cancellationToken).ConfigureAwait(false);
		var confirmed = await RunAsync(root, ["status", "--porcelain=v2", "--branch", scope == GitReviewScope.Uncommitted ? "--untracked-files=all" : "--untracked-files=no"], cancellationToken).ConfigureAwait(false);
		var current = Pact.Core.Git.GitStatusParser.Parse(confirmed.StandardOutput);
		var currentRef = baseRef is null ? null : await ResolveReviewIdAsync(root, baseRef, cancellationToken).ConfigureAwait(false);
		if (!confirmed.Succeeded || current.Branch != branch.Branch || current.Upstream != branch.Upstream
			|| currentHead != head || changes.HeadCommit != head || currentRef != refId)
		{ throw new IOException("Review refs changed during the read. Refresh to retry."); }
		var displayRef = scope == GitReviewScope.Unpushed && !fallback ? branch.Upstream : baseRef;
		return new(scope, branch.Branch, displayRef, changes, branch.Ahead, branch.Behind, fallback, null)
		{
			PullRebaseBlockedReason = scope == GitReviewScope.Uncommitted
				? await ReadPullRefusalAsync(root, current, cancellationToken).ConfigureAwait(false) : null
		};
	}

	private async Task<string?> ReadPullRefusalAsync(string root, Pact.Core.Git.GitStatusSnapshot status, CancellationToken cancellationToken)
	{
		if (status.IsDetached)
		{ return "Pull (rebase) requires an attached branch."; }
		if (status.Upstream is null)
		{ return "Configure a tracking upstream before Pull (rebase)."; }
		if (status.HasConflicts || status.IsDirty)
		{ return "Commit or stash local changes and resolve conflicts before Pull (rebase)."; }
		foreach (var name in new[] { "MERGE_HEAD", "rebase-merge", "rebase-apply" })
		{
			var result = await RunAsync(root, ["rev-parse", "--git-path", name], cancellationToken).ConfigureAwait(false);
			var path = result.StandardOutput.Trim();
			if (!result.Succeeded || path.Length == 0)
			{ return "Repository operation state is unavailable."; }
			path = Path.GetFullPath(path, root);
			if (File.Exists(path) || Directory.Exists(path))
			{ return "Finish or abort the active merge/rebase before Pull (rebase)."; }
		}
		return null;
	}

	private async Task<string?> ResolveReviewIdAsync(string root, string revision, CancellationToken cancellationToken)
	{
		var result = await RunAsync(root, ["rev-parse", "--verify", "--end-of-options", revision + "^{commit}"], cancellationToken).ConfigureAwait(false);
		var id = result.StandardOutput.Trim();
		return result.Succeeded && IsCommitId(id) ? id : null;
	}

	private async Task<string?> DefaultReviewTargetAsync(string root, string branch, CancellationToken cancellationToken)
	{
		var defaults = await RunAsync(root, ["for-each-ref", "--format=%(refname)%09%(symref)", "refs/remotes"], cancellationToken).ConfigureAwait(false);
		if (!defaults.Succeeded)
		{ throw new IOException(defaults.StandardError.Trim()); }
		var candidates = defaults.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r').Split('\t'))
			.Where(parts => parts.Length == 2 && parts[0].EndsWith("/HEAD", StringComparison.Ordinal) && parts[1].Length > 0).ToArray();
		var origin = candidates.FirstOrDefault(parts => parts[0] == "refs/remotes/origin/HEAD");
		if (origin is not null)
		{ return origin[1]; }
		if (candidates.Length == 1)
		{ return candidates[0][1]; }
		foreach (var name in new[] { "main", "master" })
		{
			if (name != branch && await ResolveReviewIdAsync(root, "refs/heads/" + name, cancellationToken).ConfigureAwait(false) is not null)
			{ return "refs/heads/" + name; }
		}
		return null;
	}

	/// <inheritdoc />
	public async Task<IReadOnlyList<GitCommitInfo>> ReadScopeCommitsAsync(string root, string? baseCommit, string headCommit, int skip, int count, CancellationToken cancellationToken)
	{
		ValidateComparison(baseCommit, headCommit);
		ArgumentOutOfRangeException.ThrowIfNegative(skip);
		ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 50);
		List<string> args = ["log", "--date-order", $"--skip={skip.ToString(CultureInfo.InvariantCulture)}",
			$"--max-count={count.ToString(CultureInfo.InvariantCulture)}", $"--format={CommitFormat}", headCommit];
		if (baseCommit is not null)
		{ args.Add("^" + baseCommit); }
		args.Add("--");
		var result = await RunAsync(root, args, cancellationToken).ConfigureAwait(false);
		if (!result.Succeeded)
		{ throw new IOException(result.StandardError.Trim()); }
		return ParseCommits(result.StandardOutput);
	}
}
using Pact.Core.Git;

namespace Pact.Presentation.Tests.Fakes;

internal sealed class CapturedCommitReader(string id, string path, string? text) : IGitCommitReader
{
	public bool Available { get; set; } = true;
	public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken ct) =>
		Task.FromResult<GitCommitInfo?>(Available && revision == id ? new(id, [], "author", DateTimeOffset.MinValue, "base") : null);
	public Task<string?> ReadCommitTextAsync(string root, string relativePath, string commitId, CancellationToken ct) =>
		Task.FromResult(Available && relativePath == path && commitId == id ? text : null);
	public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken ct) => throw new NotSupportedException();
	public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken ct) => throw new NotSupportedException();
	public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken ct) => throw new NotSupportedException();
}
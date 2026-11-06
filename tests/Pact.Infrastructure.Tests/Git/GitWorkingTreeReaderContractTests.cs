using Pact.Core.Git;

namespace Pact.Infrastructure.Tests.Git;

public sealed class GitWorkingTreeReaderContractTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task Default_summary_preserves_full_reader_clean_or_unknown_result(bool unavailable)
	{
		IGitWorkingTreeReader reader = new FullReader(unavailable);
		(await reader.ReadChangedFileCountAsync("unused", CancellationToken.None)).ShouldBe(unavailable ? null : 0);
	}

	[Test]
	public async Task Default_summary_counts_each_changed_path_once()
	{
		IGitWorkingTreeReader reader = new FullReader(false,
			[new("a", null, GitChangeKind.Modified), new("a", null, GitChangeKind.Modified, true), new("b", null, GitChangeKind.Added)]);
		(await reader.ReadChangedFileCountAsync("unused", CancellationToken.None)).ShouldBe(2);
	}

	private sealed class FullReader(bool unavailable, IReadOnlyList<GitFileEntry>? files = null) : IGitWorkingTreeReader
	{
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(unavailable ? null : new(null, "main", files ?? [], 0, 0));
		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
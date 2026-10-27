using Pact.Core.Remarks;

namespace Pact.Presentation.Services.Remarks;

/// <summary>Where a thread's line is now, or that it is outdated.</summary>
/// <param name="Line">1-based line in the text it was placed in; null when outdated.</param>
/// <param name="IsOutdated">Whether the anchor no longer matches.</param>
public sealed record RemarkPlacement(int? Line, bool IsOutdated);

/// <summary>Places remark threads in a given text or in the file as it is on disk.</summary>
public static class RemarkPlacer
{
	private static readonly RemarkPlacement Outdated = new(null, IsOutdated: true);

	/// <summary>
	/// Places a thread in <paramref name="newLines"/>, the text a shown diff was built from (New side),
	/// or against <paramref name="headCommit"/> (Old side). Null lines leave a New-side thread outdated.
	/// </summary>
	public static async Task<RemarkPlacement> PlaceInTextAsync(
		RemarkBook book,
		RemarkThread thread,
		IReadOnlyList<string>? newLines,
		string? headCommit,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(book);
		ArgumentNullException.ThrowIfNull(thread);
		var anchor = thread.Anchor;
		if (anchor.Side == RemarkSide.Old)
		{
			return RemarkLocator.IsOldValid(anchor, headCommit) ? new RemarkPlacement(anchor.Line, IsOutdated: false) : Outdated;
		}

		if (newLines is null || anchor.SnapshotId is not { } snapshotId
			|| await book.LoadSnapshotLinesAsync(snapshotId, cancellationToken).ConfigureAwait(false) is not { } captured)
		{
			return Outdated;
		}

		return RemarkLocator.LocateNew(anchor, captured, newLines) is { } line
			? new RemarkPlacement(line, IsOutdated: false)
			: Outdated;
	}

	/// <summary>Places a thread in the file as it is now, for the prompt and the agent tools.</summary>
	public static async Task<RemarkPlacement> PlaceOnDiskAsync(
		RemarkBook book,
		RemarkThread thread,
		string? headCommit,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(book);
		ArgumentNullException.ThrowIfNull(thread);
		var lines = thread.Anchor.Side == RemarkSide.New
			? await ReadLinesAsync(Path.Combine(book.ProjectRoot, thread.Anchor.Path.Replace('/', Path.DirectorySeparatorChar)), cancellationToken).ConfigureAwait(false)
			: null;
		return await PlaceInTextAsync(book, thread, lines, headCommit, cancellationToken).ConfigureAwait(false);
	}

	private static async Task<IReadOnlyList<string>?> ReadLinesAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			FileInfo file = new(path);
			return file.Exists && file.Length <= GitWorkingTreeReader.MaxFileBytes
				? RemarkLocator.SplitLines(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
				: null;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}
}

namespace Pact.Core.Platform;

/// <summary>
/// Host clipboard access. Agents that own their own selection (Claude) can only hand text
/// to Pact through the clipboard, so this is on the critical path for "send selection",
/// not just a convenience.
/// </summary>
public interface IClipboardService
{
	/// <summary>
	/// Gets a number that changes whenever any process writes the clipboard, letting callers
	/// notice a copy they did not make themselves.
	/// </summary>
	uint ChangeSequence { get; }

	/// <summary>
	/// Reads clipboard text, returning an empty string when the clipboard holds no text.
	/// </summary>
	Task<string> GetTextAsync();

	/// <summary>Reads copied local file paths; returns an empty list when no files are available.</summary>
	Task<IReadOnlyList<string>> GetFilePathsAsync() => Task.FromResult<IReadOnlyList<string>>([]);

	/// <summary>
	/// Attempts to place <paramref name="text"/> on the clipboard.
	/// </summary>
	/// <returns>
	/// <see langword="false"/> when the clipboard is unavailable or locked by another
	/// process; callers must treat failure as expected rather than exceptional.
	/// </returns>
	Task<bool> TrySetTextAsync(string text);

	/// <summary>
	/// Offers text for a client-owned paste without leaving it on the clipboard or adding it to
	/// history. Returns null when the existing clipboard cannot safely be replaced.
	/// </summary>
	Task<IClipboardPasteLease?> TryOfferPasteTextAsync(string text) => Task.FromResult<IClipboardPasteLease?>(null);

	/// <summary>Restores a pending offer after the shell has stopped its terminal processes.</summary>
	Task RestorePendingPasteAsync() => Task.CompletedTask;
}

/// <summary>Holds a paste's clipboard contents until the client has read them.</summary>
public interface IClipboardPasteLease : IAsyncDisposable
{
	/// <summary>Whether the offered contents have stayed current since the offer.</summary>
	bool IsCurrent { get; }

	/// <summary>Completes when Windows requests the offered text, or immediately for reused text.</summary>
	Task TextRequested { get; }

	/// <summary>Protects unread temporary text once a client paste may have been queued.</summary>
	void MarkPasteTriggered();
}

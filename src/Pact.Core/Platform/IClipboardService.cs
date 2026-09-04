namespace Pact.Core.Platform;

/// <summary>
/// Host clipboard access. Agents that own their own selection (Claude) can only hand text
/// to Pact through the clipboard, so this is on the critical path for "send selection",
/// not just a convenience.
/// </summary>
public interface IClipboardService
{
	/// <summary>
	/// Reads clipboard text, returning an empty string when the clipboard holds no text.
	/// </summary>
	Task<string> GetTextAsync();

	/// <summary>
	/// Attempts to place <paramref name="text"/> on the clipboard.
	/// </summary>
	/// <returns>
	/// <see langword="false"/> when the clipboard is unavailable or locked by another
	/// process; callers must treat failure as expected rather than exceptional.
	/// </returns>
	Task<bool> TrySetTextAsync(string text);
}
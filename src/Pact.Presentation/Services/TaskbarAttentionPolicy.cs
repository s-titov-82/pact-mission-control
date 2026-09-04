namespace Pact.Presentation.Services;

/// <summary>
/// Decides when a finished agent should pull the user back through the taskbar. Kept as pure
/// predicates so the rules can be tested without a window.
/// </summary>
public static class TaskbarAttentionPolicy
{
	/// <summary>
	/// Whether a completion should raise taskbar attention.
	/// </summary>
	/// <param name="markUnreadCompletion">Whether the completion is going unread.</param>
	/// <param name="wasBusyLongEnough">
	/// Whether the agent worked long enough to be worth interrupting for; short turns finish
	/// before the user has looked away.
	/// </param>
	/// <param name="isWindowActive">Whether the window is already focused.</param>
	/// <returns>
	/// <see langword="true"/> only when all three conditions favor it. An active window never
	/// raises attention, since the user is already watching.
	/// </returns>
	public static bool ShouldSetCompletionAttention(
		bool markUnreadCompletion,
		bool wasBusyLongEnough,
		bool isWindowActive) => markUnreadCompletion && wasBusyLongEnough && !isWindowActive;

	/// <summary>
	/// Whether the taskbar button should currently flash: the window is unfocused and something
	/// is waiting to be seen.
	/// </summary>
	public static bool ShouldFlashTaskbar(
		bool hasUnreadCompletions,
		bool hasCompletionAttention,
		bool isWindowActive) => !isWindowActive && (hasUnreadCompletions || hasCompletionAttention);
}
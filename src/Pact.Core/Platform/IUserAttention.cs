namespace Pact.Core.Platform;

/// <summary>
/// Signals that a background session needs the user, via the platform's taskbar attention
/// mechanism.
/// </summary>
public interface IUserAttention
{
	/// <summary>
	/// Flags the window as needing attention. Implementations must be safe to call when the
	/// window is already flagged or currently focused.
	/// </summary>
	void RequestAttention();

	/// <summary>
	/// Clears a pending attention flag; a no-op when none is set.
	/// </summary>
	void ClearAttention();
}
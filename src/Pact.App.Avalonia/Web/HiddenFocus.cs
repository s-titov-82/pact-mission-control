using Avalonia.Controls;
using Avalonia.Input;

namespace Pact.App.Avalonia.Web;

/// <summary>Keeps keyboard focus off a control that is about to be hidden.</summary>
/// <remarks>
/// Reactivating a window refocuses the element that last had the focus. A hidden WebView2 has no
/// window to take it, so the native focus call throws on the UI thread and takes the process down.
/// </remarks>
internal static class HiddenFocus
{
	/// <summary>Clears the focus when it is on <paramref name="control"/> or inside it.</summary>
	public static void Release(Control control)
	{
		ArgumentNullException.ThrowIfNull(control);
		if (control.IsKeyboardFocusWithin)
		{
			TopLevel.GetTopLevel(control)?.FocusManager?.Focus(null, NavigationMethod.Unspecified, KeyModifiers.None);
		}
	}
}

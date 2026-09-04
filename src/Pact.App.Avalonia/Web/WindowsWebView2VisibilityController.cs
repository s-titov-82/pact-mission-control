using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Pact.App.Avalonia.Web;

internal interface INativeWebViewVisibilityController
{
	void SetVisible(bool visible);

	/// <summary>
	/// Forces the native view to compose a frame for its current content.
	/// </summary>
	void RequestRepaint();
}

internal sealed class WindowsWebView2VisibilityController(
	ICoreWebView2ControllerVisibility controller) : INativeWebViewVisibilityController
{
	public void SetVisible(bool visible) => controller.SetIsVisible(visible ? 1 : 0);

	/// <summary>
	/// Nudges the controller bounds by one pixel and back. WebView2 repaints on every bounds
	/// change, which is the only reliable way to recover a view that was hidden while loading.
	/// </summary>
	public void RequestRepaint()
	{
		var bounds = controller.GetBounds();
		if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
		{
			return;
		}

		controller.SetBounds(bounds with { Bottom = bounds.Bottom - 1 });
		controller.SetBounds(bounds);
	}

	internal static unsafe INativeWebViewVisibilityController? TryCreate(NativeWebView webView)
	{
		if (webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle handle)
		{
			return null;
		}

		var controllerPointer = handle.CoreWebView2Controller;
		if (controllerPointer == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			var controller =
				ComInterfaceMarshaller<ICoreWebView2ControllerVisibility>.ConvertToManaged(
					(void*)controllerPointer);
			return controller is null ? null : new WindowsWebView2VisibilityController(controller);
		}
		finally
		{
			ComInterfaceMarshaller<ICoreWebView2ControllerVisibility>.Free((void*)controllerPointer);
		}
	}
}

/// <summary>
/// Native controller bounds in client pixels, laid out as the Win32 <c>RECT</c> WebView2 expects.
/// </summary>
internal readonly record struct WebView2ControllerBounds(
	int Left,
	int Top,
	int Right,
	int Bottom);

/// <summary>
/// The leading <c>ICoreWebView2Controller</c> vtable slots: visibility and bounds, in declaration
/// order. Members must not be reordered or removed.
/// </summary>
[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("4D00C0D1-9434-4EB6-8078-8697A560334F")]
internal partial interface ICoreWebView2ControllerVisibility
{
	int GetIsVisible();

	void SetIsVisible(int value);

	WebView2ControllerBounds GetBounds();

	void SetBounds(WebView2ControllerBounds value);
}
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Pact.App.Avalonia.Web;

/// <summary>
/// Installs <c>Web/backgroundVisibility.js</c> into a monitored browser tab so its loaded page
/// keeps updating while the tab is hidden.
/// </summary>
/// <remarks>
/// Hiding a tab makes its WebView2 controller invisible, which reports <c>document.hidden</c> and
/// stops animation frames. Pages such as TeamCity then pause live updates, so web monitoring of a
/// hidden tab never sees a build start or finish. The browser arguments that disable background
/// throttling cannot change what the page itself is told about its visibility.
/// </remarks>
internal static class WindowsWebView2BackgroundVisibility
{
	private static readonly Lazy<string?> Script = new(LoadScript);

	private const string DeactivateScript =
		"globalThis[Symbol.for('pact.backgroundVisibility')]?.setActive(false)";

	/// <summary>
	/// Registers the script for every later document.
	/// </summary>
	/// <returns>
	/// The registration id for <see cref="Unregister"/>, or null when the asset or adapter is
	/// missing and nothing was registered.
	/// </returns>
	/// <remarks>Must start on the UI thread that owns <paramref name="webView"/>.</remarks>
	public static async Task<string?> RegisterAsync(NativeWebView webView)
	{
		ArgumentNullException.ThrowIfNull(webView);
		return Script.Value is { } script && TryGetCore(webView) is { } core
			? await core.AddScriptToExecuteOnDocumentCreatedAsync(script)
			: null;
	}

	/// <summary>
	/// Stops installing the script into later documents.
	/// </summary>
	/// <remarks>Must run on the UI thread that owns <paramref name="webView"/>.</remarks>
	public static void Unregister(NativeWebView webView, string scriptId)
	{
		ArgumentNullException.ThrowIfNull(webView);
		ArgumentException.ThrowIfNullOrEmpty(scriptId);
		TryGetCore(webView)?.RemoveScriptToExecuteOnDocumentCreated(scriptId);
	}

	/// <summary>
	/// Turns the override on or off in the already loaded document. Both directions are
	/// idempotent; the script keeps one instance per document.
	/// </summary>
	/// <remarks>Must start on the UI thread that owns <paramref name="webView"/>.</remarks>
	public static async Task SetActiveInCurrentDocumentAsync(NativeWebView webView, bool active)
	{
		ArgumentNullException.ThrowIfNull(webView);
		if (Script.Value is not { } script || TryGetCore(webView) is null)
		{
			return;
		}

		await webView.InvokeScript(active ? script : DeactivateScript);
	}

	private static CoreWebView2? TryGetCore(NativeWebView webView)
	{
		if (webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle platformHandle)
		{
			return null;
		}

		var corePointer = platformHandle.CoreWebView2;
		if (corePointer == IntPtr.Zero)
		{
			return null;
		}

		try
		{
			return CoreWebView2.CreateFromComICoreWebView2(corePointer);
		}
		finally
		{
			Marshal.Release(corePointer);
		}
	}

	private static string? LoadScript()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "Web", "backgroundVisibility.js");
		return File.Exists(path) ? File.ReadAllText(path) : null;
	}
}

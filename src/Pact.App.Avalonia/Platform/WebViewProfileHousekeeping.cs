using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;

namespace Pact.App.Avalonia.Platform;

internal enum WebViewCleanupDataKinds : uint
{
	DiskCache = (uint)CoreWebView2BrowsingDataKinds.DiskCache,
	AllProfile = (uint)CoreWebView2BrowsingDataKinds.AllProfile
}

internal sealed record WebViewCleanupRequest(
	WebViewCleanupDataKinds DataKinds,
	DateTimeOffset? StartTime,
	DateTimeOffset? EndTime)
{
	public static WebViewCleanupRequest ForBrowser(DateTimeOffset now) => new(
		WebViewCleanupDataKinds.DiskCache,
		DateTimeOffset.UnixEpoch,
		now.ToUniversalTime() - TimeSpan.FromHours(72));

	public static WebViewCleanupRequest ForTerminal() => new(
		WebViewCleanupDataKinds.AllProfile,
		StartTime: null,
		EndTime: null);
}

internal interface IWebViewProfileDataCleaner
{
	Task ClearAsync(NativeWebView webView, WebViewCleanupRequest request);
}

internal sealed class WebViewProfileHousekeeping
{
	private readonly Lock _sync = new();
	private readonly IWebViewProfileDataCleaner _cleaner;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly Func<Exception, Task>? _reportFailureAsync;
	private Task? _browserCleanup;
	private Task? _terminalCleanup;

	public WebViewProfileHousekeeping(
		IWebViewProfileDataCleaner cleaner,
		Func<DateTimeOffset>? utcNow = null,
		Func<Exception, Task>? reportFailureAsync = null)
	{
		_cleaner = cleaner ?? throw new ArgumentNullException(nameof(cleaner));
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_reportFailureAsync = reportFailureAsync;
	}

	public Task EnsureBrowserProfileAsync(NativeWebView webView)
	{
		ArgumentNullException.ThrowIfNull(webView);
		lock (_sync)
		{
			return _browserCleanup ??= RunOnceAsync(
				webView,
				WebViewCleanupRequest.ForBrowser(_utcNow()));
		}
	}

	public Task EnsureTerminalProfileAsync(NativeWebView webView)
	{
		ArgumentNullException.ThrowIfNull(webView);
		lock (_sync)
		{
			return _terminalCleanup ??= RunOnceAsync(
				webView,
				WebViewCleanupRequest.ForTerminal());
		}
	}

	private async Task RunOnceAsync(NativeWebView webView, WebViewCleanupRequest request)
	{
		try
		{
			await _cleaner.ClearAsync(webView, request).ConfigureAwait(false);
		}
		catch (Exception exception)
		{
			try
			{
				if (_reportFailureAsync is not null)
				{
					await _reportFailureAsync(exception).ConfigureAwait(false);
				}
			}
			catch
			{
				// Cache cleanup and its diagnostics are both best effort.
			}
		}
	}
}

internal sealed class WindowsWebViewProfileDataCleaner : IWebViewProfileDataCleaner
{
	public async Task ClearAsync(NativeWebView webView, WebViewCleanupRequest request)
	{
		ArgumentNullException.ThrowIfNull(webView);
		ArgumentNullException.ThrowIfNull(request);
		if (webView.TryGetPlatformHandle() is not IWindowsWebView2PlatformHandle platformHandle)
		{
			throw new InvalidOperationException("The Windows WebView2 adapter has no CoreWebView2 handle.");
		}

		var corePointer = platformHandle.CoreWebView2;
		if (corePointer == IntPtr.Zero)
		{
			throw new InvalidOperationException("The Windows WebView2 adapter has no CoreWebView2 handle.");
		}

		try
		{
			var webViewAssembly = typeof(CoreWebView2BrowsingDataKinds).Assembly;
			var coreType = GetRawType(webViewAssembly, "ICoreWebView2_13");
			var profileType = GetRawType(webViewAssembly, "ICoreWebView2Profile2");
			var core = Marshal.GetTypedObjectForIUnknown(corePointer, coreType);
			var profile = GetProfile(coreType, core);
			var callbackType = webViewAssembly.GetType(
				"Microsoft.Web.WebView2.Core.Raw.ICoreWebView2ClearBrowsingDataCompletedHandler",
				throwOnError: true)!;
			var callback = DispatchProxy.Create(callbackType, typeof(CompletionDispatchProxy));
			var completion = (CompletionDispatchProxy)callback;

			if (request.DataKinds == WebViewCleanupDataKinds.AllProfile)
			{
				InvokeProfileMethod(profileType, profile, "ClearBrowsingDataAll", [callback]);
			}
			else if (request.StartTime is DateTimeOffset startTime
				&& request.EndTime is DateTimeOffset endTime)
			{
				var method = GetProfileMethod(profileType, "ClearBrowsingDataInTimeRange");
				var rawKinds = Enum.ToObject(
					method.GetParameters()[0].ParameterType,
					(uint)request.DataKinds);
				InvokeProfileMethod(
					method,
					profile,
					[rawKinds, ToUnixSeconds(startTime), ToUnixSeconds(endTime), callback]);
			}
			else
			{
				var method = GetProfileMethod(profileType, "ClearBrowsingData");
				var rawKinds = Enum.ToObject(
					method.GetParameters()[0].ParameterType,
					(uint)request.DataKinds);
				InvokeProfileMethod(method, profile, [rawKinds, callback]);
			}

			await completion.Task.ConfigureAwait(false);
		}
		finally
		{
			Marshal.Release(corePointer);
		}
	}

	private static double ToUnixSeconds(DateTimeOffset timestamp) =>
		timestamp.ToUnixTimeMilliseconds() / 1000d;

	private static Type GetRawType(Assembly assembly, string name) =>
		assembly.GetType($"Microsoft.Web.WebView2.Core.Raw.{name}", throwOnError: true)!;

	private static object GetProfile(Type coreType, object core)
	{
		var method = coreType.GetMethod("get_Profile")
			?? throw new MissingMethodException(coreType.FullName, "get_Profile");
		try
		{
			return method.Invoke(core, null)
				?? throw new InvalidOperationException("WebView2 returned no profile.");
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
			throw;
		}
	}

	private static MethodInfo GetProfileMethod(Type profileType, string name) =>
		profileType.GetMethod(name)
		?? throw new MissingMethodException(profileType.FullName, name);

	private static void InvokeProfileMethod(
		Type profileType,
		object profile,
		string name,
		object?[] arguments) =>
		InvokeProfileMethod(GetProfileMethod(profileType, name), profile, arguments);

	private static void InvokeProfileMethod(MethodInfo method, object profile, object?[] arguments)
	{
		try
		{
			method.Invoke(profile, arguments);
		}
		catch (TargetInvocationException exception) when (exception.InnerException is not null)
		{
			ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
		}
	}

	private class CompletionDispatchProxy : DispatchProxy
	{
		private readonly TaskCompletionSource _completion =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task Task => _completion.Task;

		protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
		{
			var errorCode = args is { Length: > 0 } && args[0] is int value
				? value
				: unchecked((int)0x80004005);
			if (errorCode >= 0)
			{
				_completion.TrySetResult();
				return null;
			}

			_completion.TrySetException(
				Marshal.GetExceptionForHR(errorCode)
				?? new InvalidOperationException($"WebView2 profile cleanup failed with HRESULT 0x{errorCode:X8}."));
			return null;
		}
	}
}

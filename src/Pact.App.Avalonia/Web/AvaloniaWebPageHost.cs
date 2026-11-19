using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Layout;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;

namespace Pact.App.Avalonia.Web;

internal sealed class AvaloniaWebPageHost : IWebPageHost, IWebPageProcessAttributionSource
{
	private static readonly TimeSpan AdapterReadyTimeout = TimeSpan.FromSeconds(10);
	private readonly NativeWebView _webView;
	private readonly Panel _container;
	private readonly WebViewDiagnosticTrace _diagnostics;
	private readonly Func<NativeWebView, INativeWebViewVisibilityController?> _visibilityControllerFactory;
	private readonly WebViewProfileHousekeeping? _profileHousekeeping;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<NativeWebView, string, Task<string?>> _scriptInvoker;
	private readonly IWebView2ProcessInfoReader _processInfoReader;
	private readonly Func<NativeWebView, nint?> _captureHolderWindow;
	private readonly Action<nint> _destroyHolderWindow;
	private bool _keepsRunningWhileHidden;
	private string? _backgroundVisibilityScriptId;
	private TaskCompletionSource _adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task _backgroundVisibilityUpdate = Task.CompletedTask;
	private INativeWebViewVisibilityController? _visibilityController;
	private bool _isPresented;
	private bool _isNavigationLoading;
	private bool _isNativelyVisible;
	private Border? _failureSurface;
	private long _navigationGeneration;
	private int _disposed;
	private int _eventProducersAttached = 1;

	internal AvaloniaWebPageHost(
		string id,
		NativeWebView webView,
		Panel container,
		Func<NativeWebView, INativeWebViewVisibilityController?>? visibilityControllerFactory = null,
		WebViewProfileHousekeeping? profileHousekeeping = null,
		ObservedTaskGroup? eventTasks = null,
		IUiTaskDispatcher? uiTaskDispatcher = null,
		Func<NativeWebView, string, Task<string?>>? scriptInvoker = null,
		IWebView2ProcessInfoReader? processInfoReader = null,
		Action<WebViewDiagnosticEntry>? diagnosticSink = null,
		Func<NativeWebView, nint?>? captureHolderWindow = null,
		Action<nint>? destroyHolderWindow = null)
	{
		_captureHolderWindow = captureHolderWindow ?? NativeWebViewHolderWindow.TryCapture;
		_destroyHolderWindow = destroyHolderWindow ?? NativeWebViewHolderWindow.DestroyIfOwned;
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		Id = id;
		_diagnostics = new WebViewDiagnosticTrace($"browser:{id}", diagnosticSink);
		_webView = webView;
		_container = container;
		_isPresented = webView.IsVisible;
		_visibilityControllerFactory = visibilityControllerFactory
			?? WindowsWebView2VisibilityController.TryCreate;
		_profileHousekeeping = profileHousekeeping;
		_eventTasks = eventTasks ?? new ObservedTaskGroup(
			static (_, _) => Task.CompletedTask);
		_uiTaskDispatcher = uiTaskDispatcher ?? new UiTaskDispatcher();
		_scriptInvoker = scriptInvoker ?? InvokeScript;
		_processInfoReader = processInfoReader ?? new WindowsWebView2ProcessInfoReader();
		_webView.AdapterCreated += OnAdapterCreated;
		_webView.AdapterDestroyed += OnAdapterDestroyed;
		_webView.NavigationStarted += OnNavigationStarted;
		_webView.NavigationCompleted += OnNavigationCompleted;
		_webView.NewWindowRequested += OnNewWindowRequested;
		_webView.KeyDown += OnKeyDown;
		if (_webView.TryGetPlatformHandle() is not null)
		{
			_adapterReady.TrySetResult();
		}
		RecordDiagnostic("host-created");
	}

	internal WebViewDiagnosticEntry[] DiagnosticSnapshot => _diagnostics.Snapshot();

	public string Id { get; }
	public Uri? Source { get; private set; }
	public event EventHandler<Uri>? SourceChanged;
	public event EventHandler<string>? TitleChanged;
	public event EventHandler? NavigationStarted;
	public event EventHandler? NavigationCompleted;
	public event EventHandler<string>? NavigationFailed;
	public event EventHandler<Uri>? NewWindowRequested;

	public async Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(uri);
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(async () =>
		{
			if (_keepsRunningWhileHidden)
			{
				// Start-of-document injection must finish before the page can request its first frame.
				await _adapterReady.Task.WaitAsync(AdapterReadyTimeout, cancellationToken);
				await QueueBackgroundVisibilityAsync(updateCurrentDocument: false);
			}
			cancellationToken.ThrowIfCancellationRequested();
			ObjectDisposedException.ThrowIf(_disposed != 0, this);
			Source = uri;
			_webView.Source = uri;
			RecordDiagnostic("navigation-requested", $"source={uri.AbsoluteUri}");
		});
	}

	public async Task ReloadAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(_webView.Refresh);
	}

	public async Task ShowAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_isPresented = true;
			ApplyNativeVisibility();
			RecordDiagnostic("shown");
		});
	}

	public async Task HideAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_isPresented = false;
			ApplyNativeVisibility();
			RecordDiagnostic("hidden");
		});
	}

	public async Task FocusAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() => _webView.Focus());
	}

	public async Task SetKeepsRunningWhileHiddenAsync(bool enabled, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (_keepsRunningWhileHidden != enabled)
			{
				_keepsRunningWhileHidden = enabled;
				RecordDiagnostic(enabled ? "background-visibility-on" : "background-visibility-off");
			}

			// Applied even when the state did not change, so a repeated call retries a failed one.
			return QueueBackgroundVisibilityAsync(updateCurrentDocument: Source is not null);
		});
	}

	// A settings change can arrive while WebView2 is registering the script. Keep the writes
	// in order and let a later operation retry after a failed native call.
	private Task QueueBackgroundVisibilityAsync(bool updateCurrentDocument) =>
		_backgroundVisibilityUpdate = ApplyBackgroundVisibilityAsync(_backgroundVisibilityUpdate, updateCurrentDocument);

	private async Task ApplyBackgroundVisibilityAsync(Task previous, bool updateCurrentDocument)
	{
		await previous.ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext | ConfigureAwaitOptions.SuppressThrowing);
		var adapter = _adapterReady;
		if (_disposed != 0 || !adapter.Task.IsCompletedSuccessfully)
		{
			return;
		}

		if (_keepsRunningWhileHidden)
		{
			if (_backgroundVisibilityScriptId is null)
			{
				var scriptId = await WindowsWebView2BackgroundVisibility.RegisterAsync(_webView);
				if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(adapter, _adapterReady))
				{
					return;
				}
				_backgroundVisibilityScriptId = scriptId;
			}
			if (updateCurrentDocument && _backgroundVisibilityScriptId is not null)
			{
				await WindowsWebView2BackgroundVisibility.SetActiveInCurrentDocumentAsync(_webView, active: true);
			}
		}
		else if (_backgroundVisibilityScriptId is { } scriptId)
		{
			WindowsWebView2BackgroundVisibility.Unregister(_webView, scriptId);
			if (updateCurrentDocument)
			{
				await WindowsWebView2BackgroundVisibility.SetActiveInCurrentDocumentAsync(_webView, active: false);
			}
			_backgroundVisibilityScriptId = null;
		}
	}

	/// <summary>
	/// Reads one bounded slice from a fixed application-owned outer-HTML expression.
	/// </summary>
	public async Task<WebPageDocumentFragment> ReadDocumentHtmlAsync(
		WebPageDocumentRange range,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var script = FormattableString.Invariant(
			$$"""
			(() => {
			    const root = document.documentElement;
			    const html = root === null ? "" : root.outerHTML;
			    return {
			        html: html.slice({{range.Offset}}, {{range.Offset}} + {{range.MaxChars}}),
			        totalLength: html.length
			    };
			})()
			""");
		var scriptResult = await Dispatcher.UIThread.InvokeAsync(
			() =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return _scriptInvoker(_webView, script);
			});
		return DecodeDocumentFragment(scriptResult, range);
	}

	/// <summary>
	/// Dispatches a typed application-owned monitor query to the native WebView on the Avalonia UI thread.
	/// </summary>
	public async Task<WebMonitorEvaluation> EvaluateMonitorAsync(
		WebMonitorDomQuery? query,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		var script = WebMonitorDomCodec.BuildScript(query);
		var scriptResult = await Dispatcher.UIThread.InvokeAsync(
			() =>
			{
				cancellationToken.ThrowIfCancellationRequested();
				return _webView.InvokeScript(script);
			});
		return WebMonitorDomCodec.DecodeEvaluation(query, scriptResult);
	}

	public async Task<WebViewProcessAttribution> ReadProcessAttributionAsync(
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		Task<WebViewProcessAttribution>? operation = null;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			cancellationToken.ThrowIfCancellationRequested();
			operation = _processInfoReader.ReadAsync(_webView, cancellationToken);
		});
		return await operation!.ConfigureAwait(false);
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		DetachEventProducers();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			RecordDiagnostic("host-disposing");
			_adapterReady.TrySetCanceled();
			var holderWindow = _captureHolderWindow(_webView);
			_container.Children.Remove(_webView);
			ClearFailureSurface();
			if (holderWindow is { } window)
			{
				// Removal queues the controller's disposal; the holder outlives it by design, so it
				// is destroyed here on its owning thread once the controller has closed its windows.
				DispatcherTimer.RunOnce(() => _destroyHolderWindow(window), HolderWindowDestroyDelay);
			}
		});
	}

	internal static readonly TimeSpan HolderWindowDestroyDelay = TimeSpan.FromSeconds(2);

	internal void DetachEventProducers()
	{
		if (Interlocked.Exchange(ref _eventProducersAttached, 0) == 0)
		{
			return;
		}

		_webView.AdapterCreated -= OnAdapterCreated;
		_webView.AdapterDestroyed -= OnAdapterDestroyed;
		_webView.NavigationStarted -= OnNavigationStarted;
		_webView.NavigationCompleted -= OnNavigationCompleted;
		_webView.NewWindowRequested -= OnNewWindowRequested;
		_webView.KeyDown -= OnKeyDown;
	}

	private void OnNavigationStarted(object? sender, WebViewNavigationStartingEventArgs args)
	{
		RunOnUiThreadInOrder(() =>
		{
			_navigationGeneration++;
			ClearFailureSurface();
			_isNavigationLoading = true;
			ApplyNativeVisibility();
			RecordDiagnostic("navigation-started", $"source={_webView.Source?.AbsoluteUri ?? string.Empty}");
			NavigationStarted?.Invoke(this, EventArgs.Empty);
		});
	}

	private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
	{
		RunOnUiThreadInOrder(() =>
		{
			var url = args.Request?.AbsoluteUri ?? Source?.AbsoluteUri ?? string.Empty;
			RecordDiagnostic("navigation-completed", $"source={url};success={args.IsSuccess}");
			var generation = _navigationGeneration;
			_eventTasks.TryRun("browser-navigation-result",
				() => CompleteNavigationAsync(url, args.IsSuccess, generation));
		});
	}

	private async Task CompleteNavigationAsync(string url, bool isSuccess, long generation)
	{
		var status = 0;
		var hasContent = isSuccess;
		try
		{
			var result = await _scriptInvoker(_webView, """
				(() => ({
				    status: performance.getEntriesByType('navigation')[0]?.responseStatus ?? 0,
				    hasContent: !!(document.body?.innerText?.trim() ||
				        document.body?.querySelector('img,svg,canvas,video,audio,iframe,object,embed,input,button,textarea,select'))
				}))()
				""");
			using var outer = JsonDocument.Parse(result ?? "null");
			using var document = JsonDocument.Parse(outer.RootElement.ValueKind == JsonValueKind.String
				? outer.RootElement.GetString() ?? "null" : outer.RootElement.GetRawText());
			status = document.RootElement.GetProperty("status").GetInt32();
			hasContent = document.RootElement.GetProperty("hasContent").GetBoolean();
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			RecordDiagnostic("navigation-document-unavailable", exception.GetType().Name);
		}

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (_disposed != 0 || generation != _navigationGeneration)
			{
				return;
			}

			if (!isSuccess || status >= 400)
			{
				if (!hasContent)
				{
					ShowFailureSurface(url, status);
				}
				OnLoadFailed(url, status > 0 ? status : -1, string.Empty);
			}
			else
			{
				OnNavigated(url, string.Empty);
			}
		});
	}

	private void ShowFailureSurface(string url, int status)
	{
		ClearFailureSurface();
		Button reload = new() { Content = "Reload", HorizontalAlignment = HorizontalAlignment.Left };
		reload.Click += (_, _) => _eventTasks.TryRun("browser-retry", () => ReloadAsync(CancellationToken.None));
		_failureSurface = new Border
		{
			Background = new SolidColorBrush(Color.Parse("#0f172a")),
			Padding = new global::Avalonia.Thickness(32),
			Child = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
				MaxWidth = 720,
				Spacing = 16,
				Children =
				{
					new TextBlock { Text = status > 0 ? $"Page could not be loaded (HTTP {status})" : "Page could not be loaded",
						FontSize = 20, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
					new TextBlock { Text = status > 0 ? "The server returned no visible content." : "Check the address and your network connection.",
						Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
					new TextBlock { Text = url, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap },
					reload
				}
			}
		};
		_container.Children.Add(_failureSurface);
	}

	private void ClearFailureSurface()
	{
		if (_failureSurface is { } surface)
		{
			_container.Children.Remove(surface);
			_failureSurface = null;
		}
	}

	private void OnNewWindowRequested(object? sender, WebViewNewWindowRequestedEventArgs args)
	{
		var url = args.Request?.AbsoluteUri;
		if (url is not null)
		{
			args.Handled = OnPopupOpening(url);
		}
	}

	private void OnNavigated(string url, string frameName)
	{
		RunOnUiThreadInOrder(() =>
		{
			if (!string.IsNullOrEmpty(frameName))
			{
				return;
			}

			_isNavigationLoading = false;
			ApplyNativeVisibility();
			if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				Source = uri;
				SourceChanged?.Invoke(this, uri);
			}
			NavigationCompleted?.Invoke(this, EventArgs.Empty);
			_eventTasks.TryRun(
				"web-page-title-poll",
				PollTitleAsync,
				exception =>
				{
					RecordDiagnostic("title-poll-failed", exception.GetType().Name);
					return Task.CompletedTask;
				});
		});
	}

	private async Task PollTitleAsync()
	{
		// NativeWebView has no TitleChanged event; read the document title once
		// navigation completes so IWebPageHost.TitleChanged keeps firing for callers.
		var title = await Dispatcher.UIThread.InvokeAsync(() => _webView.InvokeScript("document.title"));
		RecordDiagnostic("document-response", $"hasTitle={!string.IsNullOrEmpty(title)}");
		if (!string.IsNullOrEmpty(title))
		{
			_uiTaskDispatcher.Post(() => TitleChanged?.Invoke(this, title.Trim('"')));
		}
	}

	private void OnLoadFailed(string url, int errorCode, string frameName)
	{
		RunOnUiThreadInOrder(() =>
		{
			if (!string.IsNullOrEmpty(frameName))
			{
				return;
			}

			_isNavigationLoading = false;
			ApplyNativeVisibility();
			NavigationCompleted?.Invoke(this, EventArgs.Empty);
			NavigationFailed?.Invoke(this, $"Navigation failed ({errorCode}): {url}");
		});
	}

	private bool OnPopupOpening(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
			|| uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
		{
			return false;
		}

		_uiTaskDispatcher.Post(() => NewWindowRequested?.Invoke(this, uri));
		return true;
	}

	private void OnKeyDown(object? sender, KeyEventArgs args)
	{
		if (args.Key != Key.F5)
		{
			return;
		}

		args.Handled = true;
		_webView.Refresh();
	}

	private void OnAdapterCreated(object? sender, EventArgs args)
	{
		_visibilityController = _visibilityControllerFactory(_webView);
		if (_profileHousekeeping is not null)
		{
			_eventTasks.TryRun(
				"browser-profile-housekeeping",
				() => _profileHousekeeping.EnsureBrowserProfileAsync(_webView),
				exception =>
				{
					RecordDiagnostic(
						"profile-housekeeping-failed",
						exception.GetType().Name);
					return Task.CompletedTask;
				});
		}
		_backgroundVisibilityScriptId = null;
		_adapterReady.TrySetResult();
		if (Source is not null)
		{
			_eventTasks.TryRun(
				"browser-background-visibility",
				() => QueueBackgroundVisibilityAsync(updateCurrentDocument: true),
				exception =>
				{
					RecordDiagnostic("background-visibility-failed", exception.GetType().Name);
					return Task.CompletedTask;
				});
		}
		ApplyNativeVisibility();
		RecordDiagnostic("adapter-created");
	}

	private void OnAdapterDestroyed(object? sender, EventArgs args)
	{
		_adapterReady.TrySetCanceled();
		_adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_backgroundVisibilityScriptId = null;
		_visibilityController = null;
		_isNativelyVisible = false;
		RecordDiagnostic("adapter-destroyed");
	}

	private void ApplyNativeVisibility()
	{
		var visible = _isPresented && !_isNavigationLoading && _failureSurface is null;
		if (_failureSurface is { } surface)
		{
			surface.IsVisible = _isPresented && !_isNavigationLoading;
		}
		if (!visible)
		{
			// Every hide, a navigation's included: WebView2 crashes when a hidden control keeps focus.
			HiddenFocus.Release(_webView);
		}

		_webView.IsVisible = visible;
		_visibilityController?.SetVisible(visible);
		if (visible && !_isNativelyVisible && _visibilityController is not null)
		{
			// Workaround: a WebView2 revealed after being hidden across its navigation can stay
			// black until something forces it to compose a frame. Remove once the persisted
			// diagnostic trace identifies the real cause.
			_visibilityController.RequestRepaint();
			RecordDiagnostic("repaint-requested");
		}

		_isNativelyVisible = visible && _visibilityController is not null;
	}

	private void RunOnUiThreadInOrder(Action action) =>
		_uiTaskDispatcher.Post(action);

	private void RecordDiagnostic(string phase, string? detail = null)
	{
		var isUiThread = Dispatcher.UIThread.CheckAccess();
		_diagnostics.Record(
			phase,
			isUiThread,
			isUiThread ? _webView.IsVisible : null,
			isUiThread ? _webView.IsAttachedToVisualTree() : null,
			isUiThread ? _webView.TryGetPlatformHandle() is not null : null,
			detail);
	}

	private static WebPageDocumentFragment DecodeDocumentFragment(
		string? scriptResult,
		WebPageDocumentRange range)
	{
		if (string.IsNullOrWhiteSpace(scriptResult))
		{
			throw InvalidDocumentFragment();
		}

		try
		{
			using var outer = JsonDocument.Parse(scriptResult);
			if (outer.RootElement.ValueKind == JsonValueKind.String)
			{
				var nestedJson = outer.RootElement.GetString();
				if (string.IsNullOrWhiteSpace(nestedJson))
				{
					throw InvalidDocumentFragment();
				}

				using var nested = JsonDocument.Parse(nestedJson);
				return DecodeRoot(nested.RootElement, range);
			}

			return DecodeRoot(outer.RootElement, range);
		}
		catch (Exception exception) when (
			exception is JsonException or InvalidOperationException or ArgumentException)
		{
			throw InvalidDocumentFragment();
		}
	}

	private static WebPageDocumentFragment DecodeRoot(
		JsonElement root,
		WebPageDocumentRange range)
	{
		if (root.ValueKind != JsonValueKind.Object
			|| !root.TryGetProperty("html", out var htmlElement)
			|| htmlElement.ValueKind != JsonValueKind.String
			|| !root.TryGetProperty("totalLength", out var totalLengthElement)
			|| !totalLengthElement.TryGetInt32(out var totalLength))
		{
			throw InvalidDocumentFragment();
		}

		return WebPageDocumentFragment.Create(
			htmlElement.GetString()!,
			totalLength,
			range);
	}

	private static InvalidOperationException InvalidDocumentFragment() =>
		new("Web document capture returned an invalid fragment.");

	private static Task<string?> InvokeScript(NativeWebView view, string script) =>
		view.InvokeScript(script);
}

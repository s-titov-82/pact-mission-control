using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Microsoft.Web.WebView2.Core;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;

namespace Pact.App.Avalonia.Web;

internal sealed class AvaloniaWebPageHost : IWebPageHost, IWebPageProcessAttributionSource
{
	private static readonly TimeSpan AdapterReadyTimeout = TimeSpan.FromSeconds(10);
	private const string NativeInteractionMessage = "pact:browser-interaction";
	private const string NativeInteractionScript = """
		(() => {
		    const marker = Symbol.for('pact.browserInteraction');
		    if (window[marker]) return;
		    window[marker] = true;
		    const message = 'pact:browser-interaction';
		    const notify = () => window.chrome?.webview?.postMessage(message);
		    window.addEventListener('pointerdown', event => {
		        if (event.isTrusted) notify();
		    }, { capture: true, passive: true });
		    window.addEventListener('keydown', event => {
		        if (event.isTrusted && !event.defaultPrevented && event.key === 'Escape') notify();
		    }, { passive: true });
		})()
		""";
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
	private readonly Action? _nativeInteraction;
	private bool _keepsRunningWhileHidden;
	private string? _backgroundVisibilityScriptId;
	private TaskCompletionSource _adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private Task _backgroundVisibilityUpdate = Task.CompletedTask;
	private INativeWebViewVisibilityController? _visibilityController;
	private bool _isPresented;
	private bool _isNavigationLoading;
	private bool _isNativelyVisible;
	private bool _hasDocument;
	private Border? _failureSurface;
	private string? _failedNavigationUrl;
	private bool _reloadSuppressed;
	private CoreWebView2? _browser;
	private long _faviconGeneration;
	private const int MaxFaviconBytes = 64 * 1024;
	private ulong _browserNavigationId;
	private ulong? _downloadNavigationId;
	private string? _browserNavigationUrl;
	private readonly HashSet<CoreWebView2Frame> _interactionFrames = [];
	private (Uri Url, int Status, Task<(string Type, string? Body)> Content)? _httpError;
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
		Action<nint>? destroyHolderWindow = null,
		Action? nativeInteraction = null)
	{
		_captureHolderWindow = captureHolderWindow ?? NativeWebViewHolderWindow.TryCapture;
		_destroyHolderWindow = destroyHolderWindow ?? NativeWebViewHolderWindow.DestroyIfOwned;
		_nativeInteraction = nativeInteraction;
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
			AttachBrowser();
			_adapterReady.TrySetResult();
		}
		RecordDiagnostic("host-created");
	}

	internal WebViewDiagnosticEntry[] DiagnosticSnapshot => _diagnostics.Snapshot();

	public string Id { get; }
	public Uri? Source { get; private set; }
	public event EventHandler<Uri>? SourceChanged;
	public event EventHandler<string>? TitleChanged;
	/// <inheritdoc />
	public event EventHandler<byte[]?>? FaviconChanged;
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
			Source ??= uri;
			_webView.Source = uri;
			RecordDiagnostic("navigation-requested", $"source={uri.AbsoluteUri}");
		});
	}

	public async Task ReloadAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (_reloadSuppressed)
			{
				return;
			}

			if (_browser is { } browser && _failedNavigationUrl is { } url)
			{
				browser.Navigate(url);
			}
			else
			{
				_webView.Refresh();
			}
		});
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

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			DetachEventProducers();
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
		if (!Dispatcher.UIThread.CheckAccess())
		{
			Dispatcher.UIThread.Post(DetachEventProducers);
			return;
		}

		if (Interlocked.Exchange(ref _eventProducersAttached, 0) == 0)
		{
			return;
		}

		_webView.AdapterCreated -= OnAdapterCreated;
		DetachBrowser();
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
			if (_browser is null && _disposed == 0)
			{
				BeginNavigation(_webView.Source?.AbsoluteUri);
			}
		});
	}

	private void BeginNavigation(string? url)
	{
		_navigationGeneration++;
		_httpError = null;
		ClearFailureSurface();
		_isNavigationLoading = true;
		ApplyNativeVisibility();
		RecordDiagnostic("navigation-started", $"source={url ?? string.Empty}");
		NavigationStarted?.Invoke(this, EventArgs.Empty);
	}

	private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
	{
		RunOnUiThreadInOrder(() =>
		{
			if (_browser is not null || _disposed != 0)
			{
				return;
			}
			var url = args.Request?.AbsoluteUri ?? Source?.AbsoluteUri ?? string.Empty;
			RecordDiagnostic("navigation-completed", $"source={url};success={args.IsSuccess}");
			var generation = _navigationGeneration;
			_eventTasks.TryRun("browser-navigation-result",
				() => CompleteNavigationAsync(url, args.IsSuccess, generation));
		});
	}

	private void AttachBrowser()
	{
		DetachBrowser();
		_browser = WindowsWebView2BackgroundVisibility.TryGetCore(_webView);
		_browser?.NavigationStarting += OnBrowserNavigationStarting;
		_browser?.NavigationCompleted += OnBrowserNavigationCompleted;
		_browser?.FaviconChanged += OnBrowserFaviconChanged;
		_browser?.WebResourceResponseReceived += OnResponseReceived;
		_browser?.DownloadStarting += OnDownloadStarting;
		if (_nativeInteraction is not null && _browser is { } browser)
		{
			browser.WebMessageReceived += OnBrowserWebMessageReceived;
			browser.FrameCreated += OnBrowserFrameCreated;
			_eventTasks.TryRun("browser-interaction-registration", async () =>
			{
				await browser.AddScriptToExecuteOnDocumentCreatedAsync(NativeInteractionScript);
				// The first document can load before registration finishes.
				if (ReferenceEquals(_browser, browser))
				{
					await browser.ExecuteScriptAsync(NativeInteractionScript);
				}
			});
		}
	}

	private void DetachBrowser()
	{
		_browser?.NavigationStarting -= OnBrowserNavigationStarting;
		_browser?.NavigationCompleted -= OnBrowserNavigationCompleted;
		_browser?.FaviconChanged -= OnBrowserFaviconChanged;
		_faviconGeneration++;
		_browser?.WebResourceResponseReceived -= OnResponseReceived;
		_browser?.DownloadStarting -= OnDownloadStarting;
		_browser?.WebMessageReceived -= OnBrowserWebMessageReceived;
		_browser?.FrameCreated -= OnBrowserFrameCreated;
		foreach (var frame in _interactionFrames.ToArray())
		{
			DetachFrame(frame);
		}
		_httpError = null;
		_browser = null;
		_browserNavigationId = 0;
		_downloadNavigationId = null;
		_browserNavigationUrl = null;
	}

	private void OnBrowserFaviconChanged(object? sender, object args) =>
		RunOnUiThreadInOrder(() =>
		{
			if (_browser is not { } browser || !ReferenceEquals(sender, browser) || _disposed != 0)
			{
				return;
			}

			var generation = ++_faviconGeneration;
			var navigationGeneration = _navigationGeneration;
			var uri = browser.FaviconUri;
			if (string.IsNullOrEmpty(uri))
			{
				FaviconChanged?.Invoke(this, null);
				return;
			}

			_eventTasks.TryRun("browser-favicon", async () =>
			{
				using var image = await browser.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
				if (generation != _faviconGeneration || !IsCurrentNavigation(navigationGeneration)
					|| browser.FaviconUri != uri)
				{
					return;
				}

				var buffer = new byte[MaxFaviconBytes + 1];
				var length = image.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
				FaviconChanged?.Invoke(this, length is > 0 and <= MaxFaviconBytes ? buffer[..length] : null);
			}, exception =>
			{
				RecordDiagnostic("favicon-read-failed", exception.GetType().Name);
				return Task.CompletedTask;
			});
		});

	private void OnBrowserFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs args)
	{
		if (!ReferenceEquals(sender, _browser) && (sender is not CoreWebView2Frame parent || !_interactionFrames.Contains(parent)))
		{
			return;
		}
		var frame = args.Frame;
		if (_interactionFrames.Add(frame))
		{
			frame.WebMessageReceived += OnBrowserWebMessageReceived;
			frame.FrameCreated += OnBrowserFrameCreated;
			frame.Destroyed += OnBrowserFrameDestroyed;
		}
	}

	private void OnBrowserFrameDestroyed(object? sender, object args)
	{
		if (sender is CoreWebView2Frame frame)
		{
			DetachFrame(frame);
		}
	}

	private void DetachFrame(CoreWebView2Frame frame)
	{
		_interactionFrames.Remove(frame);
		frame.WebMessageReceived -= OnBrowserWebMessageReceived;
		frame.FrameCreated -= OnBrowserFrameCreated;
		frame.Destroyed -= OnBrowserFrameDestroyed;
	}

	private void OnBrowserWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
	{
		// A page request has only this one effect. It carries no arguments or other capabilities.
		if (_isPresented && (ReferenceEquals(sender, _browser) || sender is CoreWebView2Frame frame && _interactionFrames.Contains(frame))
			&& args.WebMessageAsJson == "\"" + NativeInteractionMessage + "\"")
		{
			_nativeInteraction?.Invoke();
		}
	}

	private void OnBrowserNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
	{
		if (!ReferenceEquals(sender, _browser))
		{
			return;
		}
		var isNewNavigation = _browserNavigationId != args.NavigationId;
		_browserNavigationId = args.NavigationId;
		_browserNavigationUrl = args.Uri;
		if (isNewNavigation)
		{
			_downloadNavigationId = null;
			BeginNavigation(args.Uri);
		}
	}

	private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
	{
		if (ReferenceEquals(sender, _browser) && _isNavigationLoading && SameUrl(_browserNavigationUrl, args.DownloadOperation.Uri))
		{
			_downloadNavigationId = _browserNavigationId;
		}
	}

	private static bool SameUrl(string? first, string? second) =>
		Uri.TryCreate(first, UriKind.Absolute, out var firstUri) &&
		Uri.TryCreate(second, UriKind.Absolute, out var secondUri) && firstUri == secondUri;

	private void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs args)
	{
		if (ReferenceEquals(sender, _browser) && args.Request.Headers.Contains("Sec-Fetch-Dest") &&
			args.Request.Headers.GetHeader("Sec-Fetch-Dest") == "document" &&
			Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var url))
		{
			if (args.Response.StatusCode >= 400)
			{
				_httpError = (url, args.Response.StatusCode, ReadErrorContentAsync(args.Response));
			}
			else if (_isNavigationLoading && SameUrl(_browserNavigationUrl, args.Request.Uri) &&
				args.Response.Headers.Contains("Content-Disposition") &&
				args.Response.Headers.GetHeader("Content-Disposition").Split(';', 2)[0].Trim()
					.Equals("attachment", StringComparison.OrdinalIgnoreCase))
			{
				_downloadNavigationId = _browserNavigationId;
			}
		}
	}

	private void OnBrowserNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
	{
		if (args.NavigationId != _browserNavigationId || !ReferenceEquals(sender, _browser) || !_isNavigationLoading)
		{
			return;
		}
		var url = _browserNavigationUrl ?? string.Empty;
		var status = args.HttpStatusCode;
		var success = args.IsSuccess;
		RecordDiagnostic("navigation-completed", $"source={url};success={success};status={status}");
		var generation = _navigationGeneration;
		if (status is 204 or 205)
		{
			CompleteWithoutDocument(url, "No page content returned", $"The server returned HTTP {status}.", generation);
			return;
		}
		if (_downloadNavigationId == args.NavigationId)
		{
			CompleteWithoutDocument(url, "Download requested", "This address starts a file download.", generation, canReload: false);
			return;
		}
		if (!success && status < 400 &&
			(args.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled ||
				args.WebErrorStatus == CoreWebView2WebErrorStatus.ConnectionAborted &&
				Uri.TryCreate(url, UriKind.Absolute, out var external) &&
				(external.Scheme is not ("http" or "https") ||
					Source is { } previous && previous != external &&
					Uri.TryCreate(_browser?.Source, UriKind.Absolute, out var current) && current == previous)))
		{
			CompleteWithoutDocument(url, "Navigation canceled", "Reload to try this address again.", generation);
			return;
		}
		if (success && status is > 0 and < 400)
		{
			OnNavigated(_browser?.Source ?? url, generation);
			return;
		}
		var response = _httpError is { } error && error.Status == status
			&& Uri.TryCreate(url, UriKind.Absolute, out var target) && error.Url.Equals(target) ? error.Content : null;
		_httpError = null;
		_eventTasks.TryRun("browser-navigation-result", () => CompleteNavigationAsync(url, success, generation, status, response));
	}

	private void CompleteWithoutDocument(string url, string heading, string detail, long generation, bool canReload = true)
	{
		if (!IsCurrentNavigation(generation))
		{
			return;
		}
		_httpError = null;
		if (!_hasDocument)
		{
			ShowNavigationMessage(url, heading, detail, canReload);
		}
		_isNavigationLoading = false;
		ApplyNativeVisibility();
		NavigationCompleted?.Invoke(this, EventArgs.Empty);
	}

	private async Task CompleteNavigationAsync(string url, bool isSuccess, long generation, int status = 0,
		Task<(string Type, string? Body)>? response = null)
	{
		var hasContent = false;
		string? body = null;
		try
		{
			var result = await _scriptInvoker(_webView, """
				(() => {
				    const type = document.contentType.toLowerCase();
				    const html = type === 'text/html' || type === 'application/xhtml+xml';
				    const body = document.body;
				    const media = 'img,svg,canvas,video,audio,iframe,object,embed,input:not([type=hidden]),button,textarea,select';
				    const visible = element => element.checkVisibility({ opacityProperty: true, visibilityProperty: true }) &&
				        element.getClientRects().length > 0;
				    let text = null;
				    if (!html && (type.startsWith('text/') || type.includes('json'))) {
				        text = (body?.querySelector('pre')?.textContent ?? body?.textContent ?? '').slice(0, 65536);
				        if (type.includes('json')) {
				            try { text = JSON.stringify(JSON.parse(text), null, 2); } catch {}
				        }
				    }
				    return JSON.stringify({
				        status: performance.getEntriesByType('navigation')[0]?.responseStatus ?? 0,
				        hasContent: html && /^https?:/i.test(document.documentURI) &&
				            performance.getEntriesByType('navigation')[0]?.responseStatus >= 400 &&
				            !!body && visible(body) && (!!body.innerText.trim() || [...body.querySelectorAll(media)].some(visible)),
				        body: text
				    });
				})()
				""");
			using var outer = JsonDocument.Parse(result ?? "null");
			using var document = JsonDocument.Parse(outer.RootElement.ValueKind == JsonValueKind.String
				? outer.RootElement.GetString() ?? "null" : outer.RootElement.GetRawText());
			if (status == 0)
			{
				status = document.RootElement.GetProperty("status").GetInt32();
			}
			hasContent = document.RootElement.GetProperty("hasContent").GetBoolean();
			if (document.RootElement.TryGetProperty("body", out var responseBody))
			{
				body = responseBody.GetString();
			}
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			RecordDiagnostic("navigation-document-unavailable", exception.GetType().Name);
		}

		if (response is not null)
		{
			var (type, text) = await response;
			if (type.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
				type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
			{
				hasContent &= text is null || !string.IsNullOrWhiteSpace(text);
			}
			else if (type.Length > 0)
			{
				hasContent = false;
				body = text ?? body;
			}
		}

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			if (!IsCurrentNavigation(generation))
			{
				return;
			}
			if (status is 204 or 205)
			{
				CompleteWithoutDocument(url, "No page content returned", $"The server returned HTTP {status}.", generation);
				return;
			}

			if (!isSuccess || status >= 400)
			{
				_hasDocument = hasContent;
				if (!hasContent)
				{
					ShowFailureSurface(url, status, body);
				}
				RecordDiagnostic("navigation-error-presented", $"status={status};surface={(hasContent ? "html" : "message")}");
				OnLoadFailed(url, status > 0 ? status : -1, generation);
			}
			else
			{
				OnNavigated(url, generation);
			}
		});
	}

	private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

	private async Task<(string Type, string? Body)> ReadErrorContentAsync(CoreWebView2WebResourceResponseView response)
	{
		var type = string.Empty;
		string? body = null;
		try
		{
			type = response.Headers.GetHeader("Content-Type").Split(';')[0].Trim();
			if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Contains("json", StringComparison.OrdinalIgnoreCase))
			{
				using var stream = await response.GetContentAsync();
				if (stream is not null)
				{
					// WebView2 supplies an apartment-bound COM stream. Read it on its owning UI thread.
					using var reader = new StreamReader(stream);
					var buffer = new char[65536];
					body = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
					if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
					{
						using var json = JsonDocument.Parse(body);
						body = JsonSerializer.Serialize(json.RootElement, IndentedJson);
					}
				}
			}
		}
		catch (Exception exception)
		{
			RecordDiagnostic("navigation-response-unavailable", $"{exception.GetType().Name} ({exception.HResult:X8})");
		}
		return (type, body);
	}
	private static readonly Lazy<Bitmap> ErrorLogo = new(() =>
	{
		using var stream = AssetLoader.Open(new Uri("avares://Pact.App.Avalonia/Assets/AppIcon.png"));
		return new Bitmap(stream);
	});

	private void ShowFailureSurface(string url, int status, string? body) => ShowNavigationMessage(
		url,
		status > 0 ? $"Page failed to load (HTTP {status})" : "Page failed to load",
		!string.IsNullOrWhiteSpace(body) ? body : status > 0
			? "The server did not provide error details." : "Check the address and your network connection.");

	private void ShowNavigationMessage(string url, string heading, string description, bool canReload = true)
	{
		ClearFailureSurface();
		_reloadSuppressed = !canReload;
		_failedNavigationUrl = canReload && url.Length > 0 ? url : null;
		var detail = new TextBlock
		{
			Text = description,
			FontSize = 16,
			Foreground = Brushes.White,
			TextWrapping = TextWrapping.Wrap
		};
		var content = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			MaxWidth = 720,
			Spacing = 16,
			Children =
			{
				new Image { Source = ErrorLogo.Value, Width = 64, Height = 64 },
				new TextBlock { Text = heading, FontSize = 18, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap },
				new ScrollViewer { MaxHeight = 320, Content = detail },
				new TextBlock { Text = url, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap }
			}
		};
		if (canReload)
		{
			Button reload = new() { Content = "Reload", HorizontalAlignment = HorizontalAlignment.Left };
			reload.Click += (_, _) => _eventTasks.TryRun("browser-retry", () => ReloadAsync(CancellationToken.None));
			content.Children.Add(reload);
		}
		_failureSurface = new Border
		{
			Background = new SolidColorBrush(Color.Parse("#0f172a")),
			Padding = new global::Avalonia.Thickness(32),
			Child = content
		};
		_container.Children.Add(_failureSurface);
	}

	private void ClearFailureSurface()
	{
		_reloadSuppressed = false;
		_failedNavigationUrl = null;
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

	private bool IsCurrentNavigation(long generation) => _disposed == 0 && generation == _navigationGeneration;

	private void OnNavigated(string url, long generation)
	{
		RunOnUiThreadInOrder(() =>
		{
			if (!IsCurrentNavigation(generation))
			{
				return;
			}

			_hasDocument = !string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase);
			_isNavigationLoading = false;
			ApplyNativeVisibility();
			if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
			{
				Source = uri;
				SourceChanged?.Invoke(this, uri);
			}
			if (!IsCurrentNavigation(generation))
			{
				return;
			}
			NavigationCompleted?.Invoke(this, EventArgs.Empty);
			_eventTasks.TryRun(
				"web-page-title-poll",
				() => PollTitleAsync(generation),
				exception =>
				{
					RecordDiagnostic("title-poll-failed", exception.GetType().Name);
					return Task.CompletedTask;
				});
		});
	}

	private async Task PollTitleAsync(long generation)
	{
		// NativeWebView has no TitleChanged event; read the document title once
		// navigation completes so IWebPageHost.TitleChanged keeps firing for callers.
		var title = await Dispatcher.UIThread.InvokeAsync(() => _webView.InvokeScript("document.title"));
		RecordDiagnostic("document-response", $"hasTitle={!string.IsNullOrEmpty(title)}");
		if (!string.IsNullOrEmpty(title))
		{
			_uiTaskDispatcher.Post(() =>
			{
				if (IsCurrentNavigation(generation))
				{
					TitleChanged?.Invoke(this, title.Trim('"'));
				}
			});
		}
	}

	private void OnLoadFailed(string url, int errorCode, long generation)
	{
		RunOnUiThreadInOrder(() =>
		{
			if (!IsCurrentNavigation(generation))
			{
				return;
			}

			_isNavigationLoading = false;
			ApplyNativeVisibility();
			NavigationCompleted?.Invoke(this, EventArgs.Empty);
			if (IsCurrentNavigation(generation))
			{
				NavigationFailed?.Invoke(this, $"Navigation failed ({errorCode}): {url}");
			}
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
		AttachBrowser();
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
		RunOnUiThreadInOrder(() =>
		{
			var wasLoading = _isNavigationLoading;
			_isNavigationLoading = false;
			_hasDocument = false;
			_navigationGeneration++;
			DetachBrowser();
			var previous = _adapterReady;
			_adapterReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_backgroundVisibilityScriptId = null;
			_visibilityController = null;
			_isNativelyVisible = false;
			previous.TrySetCanceled();
			RecordDiagnostic("adapter-destroyed");
			if (wasLoading && _disposed == 0)
			{
				NavigationCompleted?.Invoke(this, EventArgs.Empty);
			}
		});
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

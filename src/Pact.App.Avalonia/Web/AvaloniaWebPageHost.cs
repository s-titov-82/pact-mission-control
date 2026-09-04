using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;

namespace Pact.App.Avalonia.Web;

internal sealed class AvaloniaWebPageHost : IWebPageHost, IWebPageProcessAttributionSource
{
	private readonly NativeWebView _webView;
	private readonly Panel _container;
	private readonly WebViewDiagnosticTrace _diagnostics;
	private readonly Func<NativeWebView, INativeWebViewVisibilityController?> _visibilityControllerFactory;
	private readonly WebViewProfileHousekeeping? _profileHousekeeping;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<NativeWebView, string, Task<string?>> _scriptInvoker;
	private readonly IWebView2ProcessInfoReader _processInfoReader;
	private INativeWebViewVisibilityController? _visibilityController;
	private bool _isPresented;
	private bool _isNavigationLoading;
	private bool _isNativelyVisible;
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
		Action<WebViewDiagnosticEntry>? diagnosticSink = null)
	{
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
		Source = uri;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
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
			_container.Children.Remove(_webView);
		});
	}

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
			var url = _webView.Source?.AbsoluteUri ?? string.Empty;
			RecordDiagnostic("navigation-completed", $"source={url};success={args.IsSuccess}");
			if (args.IsSuccess)
			{
				OnNavigated(url, string.Empty);
			}
			else
			{
				OnLoadFailed(url, -1, string.Empty);
			}
		});
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
		ApplyNativeVisibility();
		RecordDiagnostic("adapter-created");
	}

	private void OnAdapterDestroyed(object? sender, EventArgs args)
	{
		_visibilityController = null;
		_isNativelyVisible = false;
		RecordDiagnostic("adapter-destroyed");
	}

	private void ApplyNativeVisibility()
	{
		var visible = _isPresented && !_isNavigationLoading;
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

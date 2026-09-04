using System.Diagnostics;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Presentation;
using Pact.Core.Web;

namespace Pact.App.Avalonia.Web;

internal sealed class AvaloniaTerminalWebViewHost : ITerminalWebViewHost, IAsyncDisposable
{
	private static readonly (int Columns, int Rows) DefaultSize = (120, 36);
	private const int SnapshotDebounceMs = 500;
	private readonly NativeWebView _webView;
	private readonly Dictionary<string, (int Columns, int Rows)> _sessionSizes = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _selectionLock = new(1, 1);
	private readonly WebViewDiagnosticTrace _diagnostics = new("terminal");
	private readonly TerminalOutputBridgeBatcher _outputBatcher;
	private WebViewInitializationGate? _initializationGate;
	private WebViewProfileHousekeeping? _profileHousekeeping;
	private TaskCompletionSource<string>? _selectedTextCompletionSource;
	private readonly EventHandler<WebMessageReceivedEventArgs> _onWebMessageReceived;
	private ObservedTaskGroup _eventTasks = new(
		static (_, _) => Task.CompletedTask);
	private IUiTaskDispatcher _uiTaskDispatcher = new UiTaskDispatcher();
	private int _initialized;
	private int _disposed;
	private int _eventProducersAttached = 1;
	private bool _presentationVisible;

	public AvaloniaTerminalWebViewHost(NativeWebView webView)
	{
		_webView = webView;
		_outputBatcher = new TerminalOutputBridgeBatcher(WriteOutputBatchAsync);
		_onWebMessageReceived = (_, e) =>
		{
			RecordDiagnostic("webmessage-received", TryGetMessageTypeDetail(e.Body));
			ReceiveMessage(e.Body ?? string.Empty);
		};
		_webView.AdapterCreated += OnAdapterCreated;
		_webView.AdapterDestroyed += OnAdapterDestroyed;
		_webView.NavigationCompleted += OnNavigationCompleted;
		_webView.ActualThemeVariantChanged += OnActualThemeVariantChanged;
		RecordDiagnostic("host-created");
	}

	internal WebViewDiagnosticEntry[] DiagnosticSnapshot => _diagnostics.Snapshot();
	internal TerminalOutputPerformanceSnapshot OutputPerformanceSnapshot => _outputBatcher.PerformanceSnapshot;

	internal void ConfigureProfileHousekeeping(WebViewProfileHousekeeping profileHousekeeping) =>
		_profileHousekeeping = profileHousekeeping ?? throw new ArgumentNullException(nameof(profileHousekeeping));

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		IUiTaskDispatcher uiTaskDispatcher)
	{
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
	}

	public event EventHandler<(string SessionId, string Data)>? InputReceived;
	public event EventHandler<(string SessionId, int Columns, int Rows)>? ResizeReceived;

	/// <summary>
	/// Reports debounced visible-screen text received from the matching xterm instance.
	/// </summary>
	public event EventHandler<(string SessionId, string Text, bool Stable)>? ScreenSnapshotReceived;

	public event EventHandler<(string SessionId, bool HasSelection)>? SelectionChanged;
	public event EventHandler<TerminalSelectionCompleted>? SelectionCompleted;
	public event EventHandler<string>? SelectionDismissed;
	public event EventHandler<(string SessionId, Uri Uri)>? LinkRequested;
	public event EventHandler? PasteRequested;
	public event EventHandler<TerminalCopyRequest>? CopyRequested;
	public event EventHandler? BusyOverlayActionRequested;

	public async Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(terminalPage);
		if (Interlocked.Exchange(ref _initialized, 1) != 0)
		{
			throw new InvalidOperationException("Avalonia terminal WebView host is already initialized.");
		}

		WebViewInitializationGate gate = new(terminalPage);
		_initializationGate = gate;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_webView.WebMessageReceived += _onWebMessageReceived;
			_webView.Source = terminalPage;
			RecordDiagnostic("navigation-requested", $"source={terminalPage.AbsoluteUri}");
		});

		using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		timeout.CancelAfter(TimeSpan.FromSeconds(30));
		try
		{
			await gate.Completion.WaitAsync(timeout.Token);
		}
		catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
		{
			TimeoutException timeoutException = new(
				$"Terminal WebView initialization timed out; missing: {string.Join(", ", gate.MissingSignals)}.",
				exception);
			gate.Cancel(timeoutException);
			throw timeoutException;
		}
		catch (OperationCanceledException exception)
		{
			gate.Cancel(exception);
			throw;
		}

		await ApplyCurrentThemeAsync();
		await ApplyPresentationVisibilityAsync();
	}

	public (int Columns, int Rows) GetCurrentSize(string sessionId)
	{
		lock (_sessionSizes)
		{
			return _sessionSizes.TryGetValue(sessionId, out var size)
				? size
				: DefaultSize;
		}
	}

	public Task CreateTerminalAsync(string sessionId) => ExecuteScriptWhenReadyAsync(
		$"window.agentTerminal.createTerminal({JsonSerializer.Serialize(sessionId)}, "
		+ $"{{ snapshotDebounceMs: {SnapshotDebounceMs} }});");

	public async Task ShowTerminalAsync(string sessionId)
	{
		await _outputBatcher.ActivateAndFlushAsync(sessionId);
		await ExecuteScriptWhenReadyAsync(
			$"window.agentTerminal.showTerminal({JsonSerializer.Serialize(sessionId)}, "
			+ $"{{ snapshotDebounceMs: {SnapshotDebounceMs} }});");
	}

	public Task WriteOutputAsync(string sessionId, string text) => _outputBatcher.EnqueueAsync(sessionId, text);

	/// <inheritdoc />
	public Task ResetSnapshotBaselineAsync(string sessionId) => ExecuteScriptWhenReadyAsync(
		$"window.agentTerminal.resetSnapshotBaseline({JsonSerializer.Serialize(sessionId)});");

	public async Task DisposeTerminalAsync(string sessionId)
	{
		lock (_sessionSizes)
		{
			_sessionSizes.Remove(sessionId);
		}
		await _outputBatcher.RemoveSessionAsync(sessionId);
		await ExecuteScriptWhenReadyAsync(
			$"window.agentTerminal.disposeTerminal({JsonSerializer.Serialize(sessionId)});");
	}

	public async Task<string> GetSelectedTextAsync()
	{
		await WaitUntilReadyAsync();
		await _selectionLock.WaitAsync();
		try
		{
			_selectedTextCompletionSource = new TaskCompletionSource<string>(
				TaskCreationOptions.RunContinuationsAsynchronously);
			await ExecuteScriptAsync(
				"postHostMessage({type:'selectedTextResponse',data:window.agentTerminal.getSelectedText()});");
			return await _selectedTextCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally
		{
			_selectedTextCompletionSource = null;
			_selectionLock.Release();
		}
	}

	public Task FitAsync() => ExecuteScriptWhenReadyAsync("window.agentTerminal.fit();");

	/// <summary>
	/// Enables terminal cursor animation only while the terminal surface is actually presented
	/// in the active, non-minimized application window.
	/// </summary>
	public Task SetPresentationVisibleAsync(bool visible)
	{
		_presentationVisible = visible;
		return _initializationGate is null ? Task.CompletedTask : ApplyPresentationVisibilityAsync();
	}

	public async Task FocusAsync()
	{
		await WaitUntilReadyAsync();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_webView.Focus();
			_webView.InvokeScript("window.agentTerminal.focus();");
		});
	}

	public Task SetBusyOverlayAsync(
		string message,
		bool isVisible,
		bool dimBackground,
		string? actionLabel = null) => ExecuteScriptWhenReadyAsync(
			"window.agentTerminal.setBusyOverlay("
			+ $"{JsonSerializer.Serialize(message)}, "
			+ $"{JsonSerializer.Serialize(isVisible)}, "
			+ $"{JsonSerializer.Serialize(dimBackground)}, "
			+ $"{JsonSerializer.Serialize(actionLabel)});");

	private async Task ExecuteScriptWhenReadyAsync(string script)
	{
		await WaitUntilReadyAsync();
		await ExecuteScriptAsync(script);
	}

	private Task WriteOutputBatchAsync(string sessionId, string text) => ExecuteScriptWhenReadyAsync(
		$"window.agentTerminal.writeBatch({JsonSerializer.Serialize(sessionId)}, {JsonSerializer.Serialize(text)});");

	private Task ApplyPresentationVisibilityAsync() => ExecuteScriptWhenReadyAsync(
		$"window.agentTerminal.setHostVisible({(_presentationVisible ? "true" : "false")});");

	private Task WaitUntilReadyAsync() => _initializationGate?.Completion
		?? throw new InvalidOperationException("Avalonia terminal WebView host has not been initialized.");

	private async Task ExecuteScriptAsync(string script) => await Dispatcher.UIThread.InvokeAsync(() => _webView.InvokeScript(script));

	private void ReceiveMessage(string json) =>
		WebMessageThreadRouter.Route(
			Dispatcher.UIThread.CheckAccess(),
			() => HandleMessage(json),
			action => Dispatcher.UIThread.Post(action));

	private void HandleMessage(string json)
	{
		var message = TerminalWebMessageDecoder.TryDecode(json);
		if (message is null)
		{
			return;
		}

		var type = message switch
		{
			TerminalWebMessage.Ready => "ready",
			TerminalWebMessage.Input => "input",
			TerminalWebMessage.Resize => "resize",
			TerminalWebMessage.ScreenSnapshot => "screenSnapshot",
			TerminalWebMessage.SelectionChanged => "selectionChanged",
			TerminalWebMessage.SelectionCompleted => "selectionCompleted",
			TerminalWebMessage.SelectionDismissed => "selectionDismissed",
			TerminalWebMessage.LinkRequested => "linkRequested",
			TerminalWebMessage.PasteRequested => "pasteRequested",
			TerminalWebMessage.BusyOverlayAction => "busyOverlayAction",
			TerminalWebMessage.CopySelection => "copySelection",
			TerminalWebMessage.SelectedTextResponse => "selectedTextResponse",
			_ => throw new UnreachableException(),
		};
		RecordDiagnostic("webmessage-handled", $"type={type}");

		switch (message)
		{
			case TerminalWebMessage.Ready:
				RecordDiagnostic("javascript-ready");
				_initializationGate?.ReportJavaScriptReady();
				break;
			case TerminalWebMessage.Input input:
				InputReceived?.Invoke(this, (input.SessionId, input.Data));
				break;
			case TerminalWebMessage.Resize resize:
				lock (_sessionSizes)
				{
					_sessionSizes[resize.SessionId] = (resize.Columns, resize.Rows);
				}

				ResizeReceived?.Invoke(this, (resize.SessionId, resize.Columns, resize.Rows));
				break;
			case TerminalWebMessage.ScreenSnapshot snapshot:
				ScreenSnapshotReceived?.Invoke(
					this, (snapshot.SessionId, snapshot.Text, snapshot.Stable));
				break;
			case TerminalWebMessage.SelectionChanged selection:
				SelectionChanged?.Invoke(this, (selection.SessionId, selection.HasSelection));
				break;
			case TerminalWebMessage.SelectionCompleted selection:
				SelectionCompleted?.Invoke(
					this,
					new TerminalSelectionCompleted(
						selection.SessionId,
						new TerminalSelectionAnchor(selection.X, selection.Y, selection.Revision)));
				break;
			case TerminalWebMessage.SelectionDismissed dismissed:
				SelectionDismissed?.Invoke(this, dismissed.SessionId);
				break;
			case TerminalWebMessage.LinkRequested link
				when HttpWebAddress.TryParse(link.Url, out var uri):
				LinkRequested?.Invoke(this, (link.SessionId, uri));
				break;
			case TerminalWebMessage.PasteRequested:
				PasteRequested?.Invoke(this, EventArgs.Empty);
				break;
			case TerminalWebMessage.BusyOverlayAction:
				BusyOverlayActionRequested?.Invoke(this, EventArgs.Empty);
				break;
			case TerminalWebMessage.CopySelection copy:
				CopyRequested?.Invoke(
					this,
					new TerminalCopyRequest(
						copy.SessionId,
						copy.Text,
						copy.X is { } x
							? new TerminalSelectionAnchor(x, copy.Y!.Value, copy.Revision!.Value)
							: null));
				break;
			case TerminalWebMessage.SelectedTextResponse response:
				_selectedTextCompletionSource?.TrySetResult(response.Text);
				break;
		}
	}

	private void OnAdapterCreated(object? sender, EventArgs args)
	{
		RecordDiagnostic("adapter-created");
		if (_profileHousekeeping is not null)
		{
			_eventTasks.TryRun(
				"terminal-profile-housekeeping",
				() => _profileHousekeeping.EnsureTerminalProfileAsync(_webView),
				exception =>
				{
					RecordDiagnostic(
						"profile-housekeeping-failed",
						exception.GetType().Name);
					return Task.CompletedTask;
				});
		}
	}

	private void OnAdapterDestroyed(object? sender, EventArgs args) =>
		RecordDiagnostic("adapter-destroyed");

	private void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs args)
	{
		RecordDiagnostic(
			"navigation-completed",
			$"source={_webView.Source?.AbsoluteUri ?? string.Empty};success={args.IsSuccess}");
		_initializationGate?.ReportNavigationCompleted(args.IsSuccess);
	}

	private void OnActualThemeVariantChanged(object? sender, EventArgs args)
	{
		if (_initializationGate is null || Volatile.Read(ref _disposed) != 0)
		{
			return;
		}

		_eventTasks.TryRun(
			"terminal-theme-apply",
			ApplyCurrentThemeAsync,
			exception =>
			{
				RecordDiagnostic("theme-apply-failed", exception.GetType().Name);
				return Task.CompletedTask;
			});
	}

	private async Task ApplyCurrentThemeAsync()
	{
		await WaitUntilReadyAsync();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			var themeName = _webView.ActualThemeVariant == ThemeVariant.Dark ? "dark" : "light";
			_webView.InvokeScript(
				$"window.agentTerminal.setTheme({JsonSerializer.Serialize(themeName)});");
		});
	}

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

	private static string? TryGetMessageTypeDetail(string? body)
	{
		if (string.IsNullOrEmpty(body))
		{
			return null;
		}

		try
		{
			using var document = JsonDocument.Parse(body);
			return document.RootElement.TryGetProperty("type", out var type)
				&& type.ValueKind == JsonValueKind.String
				? $"type={type.GetString()}"
				: null;
		}
		catch (JsonException)
		{
			return "invalid-json";
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
		{
			return;
		}

		ObjectDisposedException exception = new(nameof(AvaloniaTerminalWebViewHost));
		_initializationGate?.Cancel(exception);
		_selectedTextCompletionSource?.TrySetException(exception);
		await _outputBatcher.DisposeAsync();
		DetachEventProducers();
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			RecordDiagnostic("host-disposing");
		});
		_selectionLock.Dispose();
	}

	internal void DetachEventProducers()
	{
		if (Interlocked.Exchange(ref _eventProducersAttached, 0) == 0)
		{
			return;
		}

		_uiTaskDispatcher.Post(() =>
		{
			_webView.WebMessageReceived -= _onWebMessageReceived;
			_webView.AdapterCreated -= OnAdapterCreated;
			_webView.AdapterDestroyed -= OnAdapterDestroyed;
			_webView.NavigationCompleted -= OnNavigationCompleted;
			_webView.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
		});
	}
}

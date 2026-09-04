using System.Diagnostics.CodeAnalysis;
using Pact.App.Avalonia.Web;
using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Owns loaded browser hosts, their event registrations, and their corresponding live
/// monitoring registrations.
/// </summary>
internal sealed class AvaloniaWebPageCoordinator : IDisposable
{
	private readonly WebMonitorCoordinator _monitor;
	private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
	private readonly Dictionary<string, IWebPageHost> _hosts =
		new(StringComparer.Ordinal);
	private readonly Dictionary<string, WebPageViewModel> _pages =
		new(StringComparer.Ordinal);
	private readonly Dictionary<string, Action> _detachments =
		new(StringComparer.Ordinal);
	private IWebPageHost? _activeHost;
	private bool _disposed;

	/// <summary>Creates the lifecycle owner over the DI-owned monitoring coordinator.</summary>
	public AvaloniaWebPageCoordinator(WebMonitorCoordinator monitor)
	{
		ArgumentNullException.ThrowIfNull(monitor);

		_monitor = monitor;
		_monitor.StableUrlChanged += OnMonitorStableUrlChanged;
	}

	/// <summary>
	/// Gets or sets the view-owned factory used to create native browser hosts.
	/// </summary>
	public IWebPageHostFactory? HostFactory { get; set; }

	/// <summary>Raised after a loaded page reports a new source address.</summary>
	public event EventHandler<(WebPageViewModel Page, Uri Uri)>? SourceChanged;

	/// <summary>Raised after a loaded page reports a new document title.</summary>
	public event EventHandler<(WebPageViewModel Page, string Title)>? TitleChanged;

	/// <summary>Raised after a loaded page reports a navigation failure.</summary>
	public event EventHandler<(WebPageViewModel Page, string Message)>? NavigationFailed;

	/// <summary>Raised when a page starts or completes a navigation.</summary>
	public event EventHandler<(WebPageViewModel Page, bool Navigating, bool Failed)>?
		NavigationStateChanged;

	/// <summary>Raised when a loaded page requests a new browser tab.</summary>
	public event EventHandler<(WebPageViewModel Page, Uri Uri)>? NewWindowRequested;

	/// <summary>Relays a stable document URL for persistence.</summary>
	public event EventHandler<WebMonitorStableUrlChangedEventArgs>? StableUrlChanged;

	/// <summary>
	/// Ensures one host and monitoring registration exist for <paramref name="page"/>.
	/// </summary>
	/// <returns>The loaded host, or <see langword="null"/> when no factory is available.</returns>
	public async Task<IWebPageHost?> EnsureLoadedAsync(
		WebPageViewModel page,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			return await EnsureLoadedUnderGateAsync(page, cancellationToken);
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Makes a page's host active, navigating a newly created host to its saved address.
	/// </summary>
	public async Task PresentAsync(
		WebPageViewModel page,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			if (_activeHost is not null)
			{
				await _activeHost.HideAsync(cancellationToken);
				_activeHost = null;
			}

			var isNew = !_hosts.ContainsKey(page.Record.Id);
			var host = await EnsureLoadedUnderGateAsync(page, cancellationToken);
			if (host is null)
			{
				return;
			}

			_activeHost = host;
			await host.ShowAsync(cancellationToken);
			if (isNew)
			{
				await host.NavigateAsync(
					new Uri(page.Record.ResumeUrl),
					cancellationToken);
			}
			await host.FocusAsync(cancellationToken);
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Loads a paused page and navigates it in the background without presenting or focusing it.
	/// </summary>
	public async Task ResumeInBackgroundAsync(
		WebPageViewModel page,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(page);

		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			var isNew = !_hosts.ContainsKey(page.Record.Id);
			var host = await EnsureLoadedUnderGateAsync(page, cancellationToken);
			if (host is null || !isNew)
			{
				return;
			}

			try
			{
				await host.HideAsync(cancellationToken);
				await host.NavigateAsync(
					new Uri(page.Record.ResumeUrl),
					cancellationToken);
			}
			catch (Exception resumeException)
			{
				try
				{
					await CloseUnderGateAsync(
						page.Record.Id,
						deleteSnapshot: false,
						CancellationToken.None);
				}
				catch (Exception rollbackException)
				{
					throw new AggregateException(
						$"Browser page '{page.Record.Id}' failed to resume and roll back.",
						resumeException,
						rollbackException);
				}

				throw;
			}
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Reads a bounded HTML fragment from an already-loaded page without creating or presenting it.
	/// </summary>
	/// <returns>The fragment, or null when the page has no loaded host.</returns>
	public async Task<WebPageDocumentFragment?> ReadDocumentHtmlAsync(
		string pageId,
		WebPageDocumentRange range,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			return _hosts.TryGetValue(pageId, out var host)
				? await host.ReadDocumentHtmlAsync(range, cancellationToken)
				: null;
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>Hides the active host without unloading or unregistering it.</summary>
	public async Task HideActiveAsync(CancellationToken cancellationToken)
	{
		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			if (_activeHost is not null)
			{
				await _activeHost.HideAsync(cancellationToken);
				_activeHost = null;
			}
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>Presents and reloads a loaded page.</summary>
	public async Task ReloadAsync(
		WebPageViewModel page,
		CancellationToken cancellationToken)
	{
		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			if (_hosts.TryGetValue(page.Record.Id, out var host))
			{
				await host.ReloadAsync(cancellationToken);
				await host.FocusAsync(cancellationToken);
			}
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Unregisters monitoring, detaches callbacks, and disposes one loaded host.
	/// </summary>
	public async Task CloseAsync(
		string pageId,
		bool deleteSnapshot,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pageId);

		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			await CloseUnderGateAsync(pageId, deleteSnapshot, cancellationToken);
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Applies the full close sequence to every loaded host and reports failures only after all
	/// pages have been attempted.
	/// </summary>
	public async Task DisposeHostsAsync(
		bool deleteSnapshots,
		CancellationToken cancellationToken)
	{
		await _lifecycleGate.WaitAsync(cancellationToken);
		try
		{
			List<Exception> failures = [];
			foreach (var pageId in _hosts.Keys.ToArray())
			{
				try
				{
					await CloseUnderGateAsync(
						pageId,
						deleteSnapshots,
						cancellationToken);
				}
				catch (Exception exception)
				{
					failures.Add(exception);
				}
			}

			if (failures.Count > 0)
			{
				throw new AggregateException(
					"One or more browser hosts failed to close.",
					failures);
			}
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>Returns a loaded host without changing its lifecycle.</summary>
	public bool TryGetHost(string pageId, out IWebPageHost? host) =>
		_hosts.TryGetValue(pageId, out host);

	/// <summary>Whether the specified page owns the currently presented host.</summary>
	public bool IsActive(string pageId) =>
		_hosts.TryGetValue(pageId, out var host)
		&& ReferenceEquals(host, _activeHost);

	/// <summary>Tests a rule against the active loaded page.</summary>
	public Task<WebMonitorTestResult> TestAsync(
		string pageId,
		WebMonitorRule rule,
		CancellationToken cancellationToken) =>
		_monitor.TestAsync(pageId, rule, cancellationToken);

	/// <summary>Detaches native event producers before shutdown begins draining event tasks.</summary>
	public void DetachEventProducers()
	{
		foreach (var host in _hosts.Values.OfType<AvaloniaWebPageHost>())
		{
			host.DetachEventProducers();
		}
	}

	private async Task<IWebPageHost?> EnsureLoadedUnderGateAsync(
		WebPageViewModel page,
		CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_hosts.TryGetValue(page.Record.Id, out var existing))
		{
			return existing;
		}

		if (HostFactory is null)
		{
			return null;
		}

		var host = await HostFactory.CreateAsync(
			page.Record.Id,
			cancellationToken);
		_hosts.Add(page.Record.Id, host);
		_pages.Add(page.Record.Id, page);
		_detachments.Add(page.Record.Id, AttachHostEvents(page, host));
		try
		{
			await _monitor.RegisterAsync(
				page.Record.Id,
				host,
				cancellationToken);
			page.SetBrowserLoaded(true);
			return host;
		}
		catch
		{
			_hosts.Remove(page.Record.Id);
			_pages.Remove(page.Record.Id);
			DetachHostEvents(page.Record.Id);
			await host.DisposeAsync();
			throw;
		}
	}

	[SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The host removed from the ownership dictionary is disposed below on every close path; failures are aggregated only after disposal is attempted.")]
	private async Task CloseUnderGateAsync(
		string pageId,
		bool deleteSnapshot,
		CancellationToken cancellationToken)
	{
		List<Exception> failures = [];
		try
		{
			await _monitor.UnregisterAsync(
				pageId,
				deleteSnapshot,
				cancellationToken);
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}

		_pages.Remove(pageId, out var page);
		if (_hosts.Remove(pageId, out var host))
		{
			DetachHostEvents(pageId);
			if (ReferenceEquals(_activeHost, host))
			{
				_activeHost = null;
			}

			try
			{
				await host.HideAsync(cancellationToken);
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}

			try
			{
				await host.DisposeAsync();
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}
		}

		page?.SetBrowserLoaded(false);
		if (failures.Count > 0)
		{
			throw new AggregateException(
				$"Browser host '{pageId}' failed to close cleanly.",
				failures);
		}
	}

	private Action AttachHostEvents(
		WebPageViewModel page,
		IWebPageHost host)
	{
		void OnSourceChanged(object? _, Uri uri)
		{
			SourceChanged?.Invoke(this, (page, uri));
		}
		void OnTitleChanged(object? _, string title)
		{
			TitleChanged?.Invoke(this, (page, title));
		}
		void OnNavigationStarted(object? _, EventArgs __)
		{
			page.SetLoading(true);
			_monitor.SetNavigationState(page.Record.Id, navigating: true);
			NavigationStateChanged?.Invoke(
				this,
				(page, Navigating: true, Failed: false));
		}
		void OnNavigationCompleted(object? _, EventArgs __)
		{
			page.SetLoading(false);
			_monitor.SetNavigationState(page.Record.Id, navigating: false);
			NavigationStateChanged?.Invoke(
				this,
				(page, Navigating: false, Failed: false));
		}
		void OnNavigationFailed(object? _, string message)
		{
			page.SetLoading(false);
			_monitor.SetNavigationState(page.Record.Id, navigating: false);
			NavigationStateChanged?.Invoke(
				this,
				(page, Navigating: false, Failed: true));
			NavigationFailed?.Invoke(this, (page, message));
		}
		void OnNewWindowRequested(object? _, Uri uri)
		{
			NewWindowRequested?.Invoke(this, (page, uri));
		}
		void OnMonitorStatusChanged(object? _, WebMonitorStatusChangedEventArgs args)
		{
			if (string.Equals(args.WebPageId, page.Record.Id, StringComparison.Ordinal))
			{
				page.SetMonitorStatus(args.Status);
			}
		}
		void OnMonitorDiagnosticChanged(object? _, WebMonitorDiagnosticEventArgs args)
		{
			if (string.Equals(args.WebPageId, page.Record.Id, StringComparison.Ordinal))
			{
				page.SetMonitorDiagnostic(
					$"{args.WebPageId} / {args.RuleId ?? "URL probe"} / {args.Category} / "
					+ $"attempt {args.Attempt}: {args.Message}");
			}
		}

		host.SourceChanged += OnSourceChanged;
		host.TitleChanged += OnTitleChanged;
		host.NavigationStarted += OnNavigationStarted;
		host.NavigationCompleted += OnNavigationCompleted;
		host.NavigationFailed += OnNavigationFailed;
		host.NewWindowRequested += OnNewWindowRequested;
		_monitor.StatusChanged += OnMonitorStatusChanged;
		_monitor.DiagnosticChanged += OnMonitorDiagnosticChanged;

		return () =>
		{
			host.SourceChanged -= OnSourceChanged;
			host.TitleChanged -= OnTitleChanged;
			host.NavigationStarted -= OnNavigationStarted;
			host.NavigationCompleted -= OnNavigationCompleted;
			host.NavigationFailed -= OnNavigationFailed;
			host.NewWindowRequested -= OnNewWindowRequested;
			_monitor.StatusChanged -= OnMonitorStatusChanged;
			_monitor.DiagnosticChanged -= OnMonitorDiagnosticChanged;
		};
	}

	private void DetachHostEvents(string pageId)
	{
		if (_detachments.Remove(pageId, out var detach))
		{
			detach();
		}
	}

	private void OnMonitorStableUrlChanged(
		object? sender,
		WebMonitorStableUrlChangedEventArgs e) =>
		StableUrlChanged?.Invoke(this, e);

	/// <summary>Detaches monitor event relays after host shutdown has completed.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_monitor.StableUrlChanged -= OnMonitorStableUrlChanged;
		_lifecycleGate.Dispose();
	}
}

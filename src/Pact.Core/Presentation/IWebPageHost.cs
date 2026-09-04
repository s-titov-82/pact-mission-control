using Pact.Core.Web.Monitoring;

namespace Pact.Core.Presentation;

/// <summary>
/// The browser view behind one web page tab, abstracting WebView2 away from the presentation
/// layer. Hosts are per-tab and single-use: dispose ends the view for good.
/// </summary>
public interface IWebPageHost : IAsyncDisposable
{
	/// <summary>Web page id this host serves.</summary>
	string Id { get; }

	/// <summary>
	/// Currently loaded address, or <see langword="null"/> before the first navigation completes.
	/// </summary>
	Uri? Source { get; }

	/// <summary>Raised after navigation changes the loaded address.</summary>
	event EventHandler<Uri>? SourceChanged;

	/// <summary>Raised when the document title changes; used to relabel the tab.</summary>
	event EventHandler<string>? TitleChanged;

	/// <summary>Raised when a navigation begins.</summary>
	event EventHandler? NavigationStarted;

	/// <summary>
	/// Raised when a navigation finishes. Fires for both success and failure, so it signals
	/// "no longer loading" rather than "loaded successfully".
	/// </summary>
	event EventHandler? NavigationCompleted;

	/// <summary>Raised with a description when a navigation fails.</summary>
	event EventHandler<string>? NavigationFailed;

	/// <summary>
	/// Raised when the page asks for a new window. The host does not open one; the application
	/// decides whether to route the address to a tab or the external browser.
	/// </summary>
	event EventHandler<Uri>? NewWindowRequested;

	/// <summary>Navigates to <paramref name="uri"/>.</summary>
	Task NavigateAsync(Uri uri, CancellationToken cancellationToken);

	/// <summary>Reloads the current document.</summary>
	Task ReloadAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Makes the view visible. Hidden hosts keep running, so a background page continues to
	/// load and can still be monitored.
	/// </summary>
	Task ShowAsync(CancellationToken cancellationToken);

	/// <summary>Hides the view without stopping the page.</summary>
	Task HideAsync(CancellationToken cancellationToken);

	/// <summary>Moves input focus into the view.</summary>
	Task FocusAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Reads one bounded UTF-16 slice of the current document's outer HTML.
	/// </summary>
	Task<WebPageDocumentFragment> ReadDocumentHtmlAsync(
		WebPageDocumentRange range,
		CancellationToken cancellationToken);

	/// <summary>
	/// Evaluates one application-owned monitoring query against the currently loaded document.
	/// </summary>
	/// <param name="query">The typed DOM query, or <see langword="null"/> for a URL-only probe.</param>
	/// <param name="cancellationToken">
	/// Cancellation checked before scheduling and again before browser invocation; an invocation already begun is not cancellable.
	/// </param>
	/// <returns>The actual document URL and normalized observation.</returns>
	Task<WebMonitorEvaluation> EvaluateMonitorAsync(
		WebMonitorDomQuery? query,
		CancellationToken cancellationToken);
}

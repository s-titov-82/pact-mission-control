using Avalonia.Controls;
using Avalonia.Threading;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Presentation;

namespace Pact.App.Avalonia.Web;

internal sealed class AvaloniaWebPageHostFactory(Panel container) : IWebPageHostFactory
{
	private string? _userDataFolder;
	private string? _profileName;
	private WebViewProfileHousekeeping? _profileHousekeeping;
	private ObservedTaskGroup? _eventTasks;
	private IUiTaskDispatcher? _uiTaskDispatcher;
	private Action<WebViewDiagnosticEntry>? _diagnosticSink;

	internal void ConfigureEnvironment(string userDataFolder, string? profileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(userDataFolder);
		_userDataFolder = userDataFolder;
		_profileName = profileName;
	}

	internal void ConfigureProfileHousekeeping(WebViewProfileHousekeeping profileHousekeeping) =>
		_profileHousekeeping = profileHousekeeping ?? throw new ArgumentNullException(nameof(profileHousekeeping));

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		IUiTaskDispatcher uiTaskDispatcher)
	{
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
	}

	internal void ConfigureDiagnosticSink(Action<WebViewDiagnosticEntry> diagnosticSink) =>
		_diagnosticSink = diagnosticSink ?? throw new ArgumentNullException(nameof(diagnosticSink));

	public async Task<IWebPageHost> CreateAsync(string id, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		cancellationToken.ThrowIfCancellationRequested();
		NativeWebView? webView = null;
		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			webView = new NativeWebView
			{
				Focusable = true,
				IsVisible = false
			};
			if (_userDataFolder is not null)
			{
				AvaloniaWebViewEnvironment.Configure(webView, _userDataFolder, _profileName);
			}
			container.Children.Add(webView);
		});
		return new AvaloniaWebPageHost(
			id,
			webView!,
			container,
			profileHousekeeping: _profileHousekeeping,
			eventTasks: _eventTasks,
			uiTaskDispatcher: _uiTaskDispatcher,
			diagnosticSink: _diagnosticSink);
	}
}

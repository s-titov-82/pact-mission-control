using Avalonia.Controls;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;

namespace Pact.App.Avalonia.Web;

internal sealed partial class TerminalWebViewControl : UserControl, IAsyncDisposable
{
	public TerminalWebViewControl()
	{
		InitializeComponent();
		Host = new AvaloniaTerminalWebViewHost(TerminalWebView);
	}

	internal AvaloniaTerminalWebViewHost Host { get; }

	internal void ConfigureEnvironment(string userDataFolder, string profileName) =>
		AvaloniaWebViewEnvironment.Configure(TerminalWebView, userDataFolder, profileName);

	internal void ConfigureProfileHousekeeping(WebViewProfileHousekeeping profileHousekeeping) =>
		Host.ConfigureProfileHousekeeping(profileHousekeeping);

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		IUiTaskDispatcher uiTaskDispatcher) =>
		Host.ConfigureLifecycle(eventTasks, uiTaskDispatcher);

	public ValueTask DisposeAsync() => Host.DisposeAsync();
}
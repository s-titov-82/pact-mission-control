using Avalonia.Controls;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.Web;
using Pact.Core.Presentation;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class BrowserPaneView : UserControl
{
	public BrowserPaneView()
	{
		InitializeComponent();
		Factory = new AvaloniaWebPageHostFactory(BrowserHost);
	}
	public IWebPageHostFactory Factory { get; }
	public WebPageViewModel? Page
	{
		get => DataContext as WebPageViewModel;
		set => DataContext = value;
	}

	internal bool IsLoadingSurfaceVisible => LoadingSurface.IsVisible;

	internal void ConfigureEnvironment(string userDataFolder, string? profileName) =>
		((AvaloniaWebPageHostFactory)Factory).ConfigureEnvironment(userDataFolder, profileName);

	internal void ConfigureDiagnosticSink(Action<WebViewDiagnosticEntry> diagnosticSink) =>
		((AvaloniaWebPageHostFactory)Factory).ConfigureDiagnosticSink(diagnosticSink);

	internal void ConfigureProfileHousekeeping(WebViewProfileHousekeeping profileHousekeeping) =>
		((AvaloniaWebPageHostFactory)Factory).ConfigureProfileHousekeeping(profileHousekeeping);

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		IUiTaskDispatcher uiTaskDispatcher) =>
		((AvaloniaWebPageHostFactory)Factory).ConfigureLifecycle(
			eventTasks,
			uiTaskDispatcher);
}
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class WebViewHostDiagnosticsContractTests
{
	[AvaloniaTest]
	public async Task TerminalHostRetainsDiagnosticsFromConstruction()
	{
		NativeWebView webView = new();
		await using AvaloniaTerminalWebViewHost host = new(webView);

		host.DiagnosticSnapshot.ShouldContain(entry =>
			entry.Host == "terminal" && entry.Phase == "host-created");
	}

	[AvaloniaTest]
	public async Task BrowserHostRetainsDiagnosticsFromConstruction()
	{
		Grid container = new();
		var host = (AvaloniaWebPageHost)await
			new AvaloniaWebPageHostFactory(container).CreateAsync("page-1", CancellationToken.None);

		host.DiagnosticSnapshot.ShouldContain(entry =>
			entry.Host == "browser:page-1" && entry.Phase == "host-created");

		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task BrowserHostForwardsDiagnosticsToTheConfiguredSink()
	{
		List<WebViewDiagnosticEntry> recorded = [];
		Grid container = new();
		AvaloniaWebPageHostFactory factory = new(container);
		factory.ConfigureDiagnosticSink(recorded.Add);

		var host = await factory.CreateAsync("page-1", CancellationToken.None);
		await host.NavigateAsync(new Uri("https://example.test/path"), CancellationToken.None);

		recorded.ShouldContain(entry =>
			entry.Host == "browser:page-1" && entry.Phase == "host-created");
		recorded.ShouldContain(entry => entry.Phase == "navigation-requested");

		await host.DisposeAsync();
	}
}
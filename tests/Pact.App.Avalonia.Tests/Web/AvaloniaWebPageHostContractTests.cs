using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Web;
using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class AvaloniaWebPageHostContractTests
{
	[AvaloniaTest]
	public void BrowserFactoryAcceptsDefaultWebView2Profile()
	{
		AvaloniaWebPageHostFactory factory = new(new Grid());

		factory.ConfigureEnvironment(Path.Combine("C:\\preview", "webview2"), profileName: null);
	}

	[AvaloniaTest]
	public async Task HostNavigatesShowsHidesReloadsAndDisposesBehindPortableContract()
	{
		Grid container = new();
		var host = await new AvaloniaWebPageHostFactory(container).CreateAsync("page-1", CancellationToken.None);
		Uri target = new("https://example.test/path");

		container.Children.Single().IsVisible.ShouldBeFalse();

		await host.NavigateAsync(target, CancellationToken.None);
		await host.ShowAsync(CancellationToken.None);
		container.Children.Single().IsVisible.ShouldBeTrue();
		await host.FocusAsync(CancellationToken.None);
		await host.HideAsync(CancellationToken.None);
		container.Children.Single().IsVisible.ShouldBeFalse();

		host.Source.ShouldBe(target);
		container.Children.ShouldHaveSingleItem();
		await host.DisposeAsync();
		container.Children.ShouldBeEmpty();
	}

	[AvaloniaTest]
	public async Task Document_capture_uses_fixed_script_and_preserves_utf16_fragment_exactly()
	{
		string? invokedScript = null;
		NativeWebView webView = new();
		AvaloniaWebPageHost host = new(
			"page-1",
			webView,
			new Grid(),
			scriptInvoker: (_, script) =>
			{
				invokedScript = script;
				return Task.FromResult<string?>(
					"""{"html":" \ud83d\ude00\n","totalLength":9}""");
			});

		var fragment = await host.ReadDocumentHtmlAsync(
			new WebPageDocumentRange(2, 4),
			CancellationToken.None);

		invokedScript.ShouldNotBeNull().ShouldContain("document.documentElement");
		invokedScript.ShouldContain("slice(2, 2 + 4)");
		fragment.ShouldBe(new WebPageDocumentFragment(
			" 😀\n",
			TotalLength: 9,
			NextOffset: 6));
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task MainFrameEventsPropagateSourceAndFailure()
	{
		Grid container = new();
		var host = await new AvaloniaWebPageHostFactory(container).CreateAsync("page-1", CancellationToken.None);
		Uri? changed = null;
		Uri? popup = null;
		var completed = false;
		string? failure = null;
		host.SourceChanged += (_, uri) => changed = uri;
		host.NavigationCompleted += (_, _) => completed = true;
		host.NewWindowRequested += (_, uri) => popup = uri;
		host.NavigationFailed += (_, message) => failure = message;
		var type = host.GetType();

		type.GetMethod("OnNavigated", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, ["https://example.test/next", string.Empty]);
		type.GetMethod("OnLoadFailed", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, ["https://example.test/bad", -7, string.Empty]);
		type.GetMethod("OnPopupOpening", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, ["https://example.test/popup"]);
		await Dispatcher.UIThread.InvokeAsync(static () => { });

		(changed?.AbsoluteUri.TrimEnd('/')).ShouldBe("https://example.test/next");
		(popup?.AbsoluteUri.TrimEnd('/')).ShouldBe("https://example.test/popup");
		completed.ShouldBeTrue();
		failure!.Contains("-7", StringComparison.Ordinal).ShouldBeTrue();
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task PopupRequestIsHandledWhenPromotedToSavedWebPage()
	{
		Grid container = new();
		var host = await new AvaloniaWebPageHostFactory(container).CreateAsync("page-1", CancellationToken.None);
		Uri? popup = null;
		host.NewWindowRequested += (_, uri) => popup = uri;
		WebViewNewWindowRequestedEventArgs args = new()
		{
			Request = new Uri("https://example.test/popup")
		};

		host.GetType().GetMethod("OnNewWindowRequested", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, [null, args]);
		await Dispatcher.UIThread.InvokeAsync(static () => { });

		args.Handled.ShouldBeTrue();
		(popup?.AbsoluteUri.TrimEnd('/')).ShouldBe("https://example.test/popup");
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task NativeWebViewControllerIsVisibleOnlyWhileWebPageIsVisible()
	{
		Grid container = new();
		NativeWebView webView = new();
		RecordingNativeWebViewVisibilityController visibilityController = new();
		AvaloniaWebPageHost host = new(
			"page-1",
			webView,
			container,
			_ => visibilityController);

		host.GetType().GetMethod("OnAdapterCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, [null, EventArgs.Empty]);
		await host.HideAsync(CancellationToken.None);
		await host.ShowAsync(CancellationToken.None);

		visibilityController.States.ShouldBe([true, false, true]);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task NativeSurfaceIsHiddenUntilNavigationCompletes()
	{
		RecordingNativeWebViewVisibilityController visibilityController = new();
		var host = CreateHost(visibilityController);
		RaiseAdapterCreated(host);

		RaiseNavigationStarted(host);
		RaiseNavigated(host, "https://example.test/ready");

		visibilityController.States.ShouldBe([true, false, true]);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task NavigationLifecycleNotificationsPreserveWebViewCallbackOrder()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		List<string> notifications = [];
		host.NavigationStarted += (_, _) => notifications.Add("started");
		host.NavigationCompleted += (_, _) => notifications.Add("completed");

		RaiseNavigationStarted(host);
		RaiseNavigated(host, "https://example.test/ready");

		notifications.ShouldBe(["started", "completed"]);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task NavigationLifecycleNotificationsAreMarshaledToTheUiThreadInOrder()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		List<(string Phase, bool IsUiThread)> notifications = [];
		host.NavigationStarted += (_, _) =>
			notifications.Add(("started", Dispatcher.UIThread.CheckAccess()));
		host.NavigationCompleted += (_, _) =>
			notifications.Add(("completed", Dispatcher.UIThread.CheckAccess()));

		await Task.Run(() =>
		{
			RaiseNavigationStarted(host);
			RaiseNavigated(host, "https://example.test/ready");
		});

		notifications.ShouldBe([("started", true), ("completed", true)]);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task TitlePollingFailureIsReportedWithoutReorderingNavigationLifecycle()
	{
		TaskCompletionSource<Exception> reported = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup group = new((_, exception) =>
		{
			reported.TrySetResult(exception);
			return Task.CompletedTask;
		});
		AvaloniaWebPageHost host = new(
			"page-1",
			new NativeWebView(),
			new Grid(),
			_ => new RecordingNativeWebViewVisibilityController(),
			eventTasks: group,
			uiTaskDispatcher: new ImmediateUiTaskDispatcher());
		List<string> notifications = [];
		host.NavigationStarted += (_, _) => notifications.Add("started");
		host.NavigationCompleted += (_, _) => notifications.Add("completed");

		RaiseNavigationStarted(host);
		RaiseNavigated(host, "https://example.test/ready");
		await group.CompleteAndDrainAsync();

		notifications.ShouldBe(["started", "completed"]);
		(await reported.Task).ShouldBeOfType<InvalidOperationException>();
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task NavigationCompletionEndsLoadingBeforeAnAbsoluteSourceIsAvailable()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		List<string> notifications = [];
		host.NavigationStarted += (_, _) => notifications.Add("started");
		host.NavigationCompleted += (_, _) => notifications.Add("completed");

		RaiseNavigationStarted(host);
		RaiseNavigated(host, string.Empty);

		notifications.ShouldBe(["started", "completed"]);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task CompletingNavigationDoesNotRevealHiddenPage()
	{
		RecordingNativeWebViewVisibilityController visibilityController = new();
		var host = CreateHost(visibilityController);
		RaiseAdapterCreated(host);
		RaiseNavigationStarted(host);
		await host.HideAsync(CancellationToken.None);

		RaiseNavigated(host, "https://example.test/ready");

		visibilityController.States[^1].ShouldBeFalse();
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task RevealingThePageAfterNavigationRequestsANativeRepaint()
	{
		RecordingNativeWebViewVisibilityController visibilityController = new();
		var host = CreateHost(visibilityController);
		RaiseAdapterCreated(host);
		await host.ShowAsync(CancellationToken.None);
		var repaintsBeforeNavigation = visibilityController.RepaintRequests;

		RaiseNavigationStarted(host);
		RaiseNavigated(host, "https://example.test/ready");

		visibilityController.States[^1].ShouldBeTrue();
		visibilityController.RepaintRequests.ShouldBe(repaintsBeforeNavigation + 1);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task StayingHiddenAfterNavigationRequestsNoNativeRepaint()
	{
		RecordingNativeWebViewVisibilityController visibilityController = new();
		var host = CreateHost(visibilityController);
		RaiseAdapterCreated(host);
		await host.HideAsync(CancellationToken.None);
		var repaintsBeforeNavigation = visibilityController.RepaintRequests;

		RaiseNavigationStarted(host);
		RaiseNavigated(host, "https://example.test/ready");

		visibilityController.RepaintRequests.ShouldBe(repaintsBeforeNavigation);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task MonitorEvaluationChecksCancellationBeforeWebViewDispatch()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		using CancellationTokenSource cancellation = new();
		cancellation.Cancel();

		await Should.ThrowAsync<OperationCanceledException>(
			() => host.EvaluateMonitorAsync(query: null, cancellation.Token));

		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task MonitorEvaluationChecksCancellationAgainAfterUiDispatchWasQueued()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		using CancellationTokenSource cancellation = new();
		using ManualResetEventSlim dispatchQueued = new();

		var evaluation = Task.Run(async () =>
		{
			var pending =
				host.EvaluateMonitorAsync(query: null, cancellation.Token);
			dispatchQueued.Set();
			return await pending;
		});

		dispatchQueued.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
		cancellation.Cancel();

		await Should.ThrowAsync<OperationCanceledException>(() => evaluation);
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task Process_attribution_is_read_through_the_existing_native_host()
	{
		WebViewProcessAttribution expected = new([20], [100, 101]);
		RecordingWebView2ProcessInfoReader reader = new(expected);
		AvaloniaWebPageHost host = new(
			"page-1",
			new NativeWebView(),
			new Grid(),
			processInfoReader: reader);

		var actual = await host.ReadProcessAttributionAsync(CancellationToken.None);

		actual.ShouldBeSameAs(expected);
		reader.ReadCount.ShouldBe(1);
		reader.WasOnUiThread.ShouldBeTrue();
		await host.DisposeAsync();
	}

	[AvaloniaTest]
	public async Task Native_process_reader_reports_an_uninitialized_adapter_as_unavailable()
	{
		WindowsWebView2ProcessInfoReader reader = new();

		var action = () => reader.ReadAsync(new NativeWebView(), CancellationToken.None);

		var exception = await action.ShouldThrowAsync<InvalidOperationException>();
		exception.Message.ShouldBe("The selected web tab has no active WebView2 adapter.");
	}

	[AvaloniaTest]
	public async Task MonitorEvaluationDelegatesDirectlyToNativeWebView()
	{
		var host = CreateHost(new RecordingNativeWebViewVisibilityController());
		WebMonitorDomQuery query = new(
			Activity: null,
			Revision: null,
			ActivityWhenExtractorMissing: false);

		var exception = await Should.ThrowAsync<InvalidOperationException>(
			() => host.EvaluateMonitorAsync(query, CancellationToken.None));

		exception.Message.ShouldContain("Unable to invoke script before any page was loaded");
		await host.DisposeAsync();
	}

	private static AvaloniaWebPageHost CreateHost(
		RecordingNativeWebViewVisibilityController visibilityController) =>
		new("page-1", new NativeWebView(), new Grid(), _ => visibilityController);

	private static void RaiseAdapterCreated(AvaloniaWebPageHost host) =>
		host.GetType().GetMethod("OnAdapterCreated", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, [null, EventArgs.Empty]);

	private static void RaiseNavigationStarted(AvaloniaWebPageHost host) =>
		host.GetType().GetMethod("OnNavigationStarted", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, [null, null]);

	private static void RaiseNavigated(AvaloniaWebPageHost host, string url) =>
		host.GetType().GetMethod("OnNavigated", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(host, [url, string.Empty]);

	private sealed class RecordingNativeWebViewVisibilityController : INativeWebViewVisibilityController
	{
		public List<bool> States { get; } = [];

		public int RepaintRequests { get; private set; }

		public void SetVisible(bool visible) => States.Add(visible);

		public void RequestRepaint() => RepaintRequests++;
	}

	private sealed class RecordingWebView2ProcessInfoReader(
		WebViewProcessAttribution attribution) : IWebView2ProcessInfoReader
	{
		internal int ReadCount { get; private set; }

		internal bool WasOnUiThread { get; private set; }

		public Task<WebViewProcessAttribution> ReadAsync(
			NativeWebView webView,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			ReadCount++;
			WasOnUiThread = Dispatcher.UIThread.CheckAccess();
			return Task.FromResult(attribution);
		}
	}
}

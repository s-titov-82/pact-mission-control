using Pact.App.Avalonia.Controllers;
using Pact.Core.Presentation;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaWebPageCoordinatorTests
{
	[Test]
	public async Task MonitorProjectionUpdatesTheOwnedRootPage()
	{
		await using CoordinatorFixture fixture = new(isRootItem: true);
		fixture.Host.MonitorEvaluation = new(
			new Uri("https://example.com/"),
			new WebMonitorObservation(Activity: true, Revision: null));
		await fixture.Monitor.SetRulesAsync(
			[CreateRule()],
			CancellationToken.None);
		TaskCompletionSource activityProjected =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Page.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(WebPageViewModel.IsMonitorActive)
				&& fixture.Page.IsMonitorActive)
			{
				activityProjected.TrySetResult();
			}
		};

		await fixture.Coordinator.EnsureLoadedAsync(
			fixture.Page,
			CancellationToken.None);
		await activityProjected.Task.WaitAsync(TimeSpan.FromSeconds(3));

		fixture.Page.IsRootItem.ShouldBeTrue();
		fixture.Page.MonitorStatus.ShouldBe(WebMonitorStatus.Activity);
		fixture.Page.ShowMonitorActivity.ShouldBeTrue();
	}

	[Test]
	public async Task ClosingPageUnregistersMonitorBeforeDisposingHost()
	{
		await using CoordinatorFixture fixture = new();
		await fixture.Coordinator.EnsureLoadedAsync(
			fixture.Page,
			CancellationToken.None);
		WebMonitorTestResult? duringDispose = null;
		fixture.Host.Disposing = async () =>
		{
			duringDispose = await fixture.Monitor.TestAsync(
				fixture.Page.Record.Id,
				CreateRule(),
				CancellationToken.None);
		};

		await fixture.Coordinator.CloseAsync(
			fixture.Page.Record.Id,
			deleteSnapshot: true,
			CancellationToken.None);

		duringDispose.ShouldNotBeNull();
		duringDispose.Error.ShouldBe("No loaded web tab is registered for testing.");
		fixture.Host.DisposeCount.ShouldBe(1);
		fixture.Page.IsBrowserLoaded.ShouldBeFalse();
	}

	[Test]
	public async Task PresentingNewPageShowsNavigatesAndFocusesSingleOwnedHost()
	{
		await using CoordinatorFixture fixture = new();

		await fixture.Coordinator.PresentAsync(
			fixture.Page,
			CancellationToken.None);

		fixture.Host.Calls.ShouldBe(["show", "navigate", "focus"]);
		fixture.Host.NavigatedUri.ShouldBe(new Uri(fixture.Page.Record.ResumeUrl));
		fixture.Coordinator.IsActive(fixture.Page.Record.Id).ShouldBeTrue();
	}

	[Test]
	public async Task ResumeInBackground_navigates_new_host_without_show_or_focus()
	{
		await using CoordinatorFixture fixture = new();

		await fixture.Coordinator.ResumeInBackgroundAsync(
			fixture.Page,
			CancellationToken.None);

		fixture.Host.Calls.ShouldBe(["hide", "navigate"]);
		fixture.Coordinator.IsActive(fixture.Page.Record.Id).ShouldBeFalse();
		fixture.Page.IsBrowserLoaded.ShouldBeTrue();
	}

	[Test]
	public async Task ResumeInBackground_navigation_failure_rolls_back_host_and_allows_retry()
	{
		FakeWebPageHost failedHost = new("web-1")
		{
			Navigating = static (_, _) =>
				Task.FromException(new InvalidOperationException("Navigation failed."))
		};
		FakeWebPageHost retryHost = new("web-1");
		await using CoordinatorFixture fixture = new(failedHost, retryHost);

		await Should.ThrowAsync<InvalidOperationException>(() =>
			fixture.Coordinator.ResumeInBackgroundAsync(
				fixture.Page,
				CancellationToken.None));

		fixture.Page.IsBrowserLoaded.ShouldBeFalse();
		failedHost.DisposeCount.ShouldBe(1);

		await fixture.Coordinator.ResumeInBackgroundAsync(
			fixture.Page,
			CancellationToken.None);

		fixture.Factory.CreateCount.ShouldBe(2);
		retryHost.Calls.ShouldBe(["hide", "navigate"]);
		fixture.Page.IsBrowserLoaded.ShouldBeTrue();
	}

	[Test]
	public async Task ResumeInBackground_cancellation_rolls_back_host_and_allows_retry()
	{
		using CancellationTokenSource navigationCancellation = new();
		FakeWebPageHost canceledHost = new("web-1")
		{
			Navigating = (_, _) =>
			{
				navigationCancellation.Cancel();
				return Task.FromCanceled(navigationCancellation.Token);
			}
		};
		FakeWebPageHost retryHost = new("web-1");
		await using CoordinatorFixture fixture = new(canceledHost, retryHost);

		await Should.ThrowAsync<OperationCanceledException>(() =>
			fixture.Coordinator.ResumeInBackgroundAsync(
				fixture.Page,
				CancellationToken.None));

		fixture.Page.IsBrowserLoaded.ShouldBeFalse();
		canceledHost.DisposeCount.ShouldBe(1);

		await fixture.Coordinator.ResumeInBackgroundAsync(
			fixture.Page,
			CancellationToken.None);

		fixture.Factory.CreateCount.ShouldBe(2);
		retryHost.Calls.ShouldBe(["hide", "navigate"]);
		fixture.Page.IsBrowserLoaded.ShouldBeTrue();
	}

	[Test]
	public async Task ReadDocumentHtml_returns_from_an_existing_host_without_creating_one()
	{
		await using CoordinatorFixture fixture = new();
		var range = new WebPageDocumentRange(1, 3);
		fixture.Host.DocumentFragment = new(
			"abc",
			TotalLength: 8,
			NextOffset: 4);

		var beforeLoad = await fixture.Coordinator.ReadDocumentHtmlAsync(
			fixture.Page.Record.Id,
			range,
			CancellationToken.None);
		await fixture.Coordinator.EnsureLoadedAsync(
			fixture.Page,
			CancellationToken.None);
		var loaded = await fixture.Coordinator.ReadDocumentHtmlAsync(
			fixture.Page.Record.Id,
			range,
			CancellationToken.None);

		beforeLoad.ShouldBeNull();
		fixture.Factory.CreateCount.ShouldBe(1);
		loaded.ShouldBe(fixture.Host.DocumentFragment);
		fixture.Host.DocumentRanges.ShouldBe([range]);
	}

	[Test]
	public async Task ReadDocumentHtml_is_serialized_against_host_close()
	{
		await using CoordinatorFixture fixture = new();
		await fixture.Coordinator.EnsureLoadedAsync(
			fixture.Page,
			CancellationToken.None);
		TaskCompletionSource readStarted =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseRead =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Host.ReadDocument = async (_, cancellationToken) =>
		{
			readStarted.TrySetResult();
			await releaseRead.Task.WaitAsync(cancellationToken);
			return new WebPageDocumentFragment("abc", 3, NextOffset: null);
		};

		var read = fixture.Coordinator.ReadDocumentHtmlAsync(
			fixture.Page.Record.Id,
			new WebPageDocumentRange(0, 3),
			CancellationToken.None);
		await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var close = fixture.Coordinator.CloseAsync(
			fixture.Page.Record.Id,
			deleteSnapshot: false,
			CancellationToken.None);

		close.IsCompleted.ShouldBeFalse();
		fixture.Host.DisposeCount.ShouldBe(0);
		releaseRead.TrySetResult();
		await read;
		await close;

		fixture.Host.DisposeCount.ShouldBe(1);
	}

	private static WebMonitorRule CreateRule() => new(
		"rule",
		"Rule",
		Enabled: true,
		UrlPattern: ".*",
		PollIntervalSeconds: 60,
		Activity: new WebMonitorExtractor(
			".activity",
			WebMonitorValueSource.Exists,
			AttributeName: null,
			MatchPattern: null,
			CaptureGroup: null),
		Revision: null);

	private sealed class CoordinatorFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory =
			TemporaryDirectory.Create();

		public CoordinatorFixture(params IWebPageHost[] hosts)
			: this(isRootItem: false, hosts)
		{
		}

		public CoordinatorFixture(bool isRootItem, params IWebPageHost[] hosts)
		{
			AppPaths paths = new(_temporaryDirectory.Path);
			Monitor = new WebMonitorCoordinator(
				new WebMonitorSnapshotStore(paths),
				TimeProvider.System,
				static action => action());
			Host = hosts.FirstOrDefault() as FakeWebPageHost
				?? new FakeWebPageHost("web-1");
			Factory = new FakeWebPageHostFactory(
				hosts.Length == 0 ? [Host] : hosts);
			Coordinator = new AvaloniaWebPageCoordinator(Monitor)
			{
				HostFactory = Factory
			};
			var now = DateTimeOffset.UtcNow;
			Page = new WebPageViewModel(
				new WebPageRecord(
					"web-1",
					"Example",
					"https://example.com/",
					"https://example.com/current",
					now,
					now),
				isRootItem);
		}

		public WebMonitorCoordinator Monitor { get; }
		public FakeWebPageHost Host { get; }
		public FakeWebPageHostFactory Factory { get; }
		public AvaloniaWebPageCoordinator Coordinator { get; }
		public WebPageViewModel Page { get; }

		public async ValueTask DisposeAsync()
		{
			await Coordinator.DisposeHostsAsync(
				deleteSnapshots: false,
				CancellationToken.None);
			Coordinator.Dispose();
			await Monitor.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}
	}

	private sealed class FakeWebPageHostFactory(IEnumerable<IWebPageHost> hosts)
		: IWebPageHostFactory
	{
		private readonly Queue<IWebPageHost> _hosts = new(hosts);

		public int CreateCount { get; private set; }

		public Task<IWebPageHost> CreateAsync(
			string id,
			CancellationToken cancellationToken)
		{
			CreateCount++;
			return Task.FromResult(_hosts.Dequeue());
		}
	}

	private sealed class FakeWebPageHost(string id) : IWebPageHost
	{
		public string Id { get; } = id;
		public Uri? Source { get; private set; } = new("https://example.com/");
		public List<string> Calls { get; } = [];
		public Uri? NavigatedUri { get; private set; }
		public Func<Task>? Disposing { get; set; }
		public Func<Uri, CancellationToken, Task>? Navigating { get; set; }
		public Func<WebPageDocumentRange, CancellationToken, Task<WebPageDocumentFragment>>?
			ReadDocument
		{ get; set; }
		public WebPageDocumentFragment DocumentFragment { get; set; } =
			new(string.Empty, 0, NextOffset: null);
		public WebMonitorEvaluation MonitorEvaluation { get; set; } = new(
			new Uri("https://example.com/"),
			new WebMonitorObservation(Activity: false, Revision: null));
		public List<WebPageDocumentRange> DocumentRanges { get; } = [];
		public int DisposeCount { get; private set; }

		public event EventHandler<Uri>? SourceChanged { add { } remove { } }
		public event EventHandler<string>? TitleChanged { add { } remove { } }
		public event EventHandler? NavigationStarted { add { } remove { } }
		public event EventHandler? NavigationCompleted { add { } remove { } }
		public event EventHandler<string>? NavigationFailed { add { } remove { } }
		public event EventHandler<Uri>? NewWindowRequested { add { } remove { } }

		public Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
		{
			Calls.Add("navigate");
			NavigatedUri = uri;
			Source = uri;
			return Navigating?.Invoke(uri, cancellationToken)
				?? Task.CompletedTask;
		}

		public Task ReloadAsync(CancellationToken cancellationToken)
		{
			Calls.Add("reload");
			return Task.CompletedTask;
		}

		public Task ShowAsync(CancellationToken cancellationToken)
		{
			Calls.Add("show");
			return Task.CompletedTask;
		}

		public Task HideAsync(CancellationToken cancellationToken)
		{
			Calls.Add("hide");
			return Task.CompletedTask;
		}

		public Task FocusAsync(CancellationToken cancellationToken)
		{
			Calls.Add("focus");
			return Task.CompletedTask;
		}

		public Task<WebMonitorEvaluation> EvaluateMonitorAsync(
			WebMonitorDomQuery? query,
			CancellationToken cancellationToken) =>
			Task.FromResult(MonitorEvaluation);

		public Task<WebPageDocumentFragment> ReadDocumentHtmlAsync(
			WebPageDocumentRange range,
			CancellationToken cancellationToken)
		{
			DocumentRanges.Add(range);
			return ReadDocument is null
				? Task.FromResult(DocumentFragment)
				: ReadDocument(range, cancellationToken);
		}

		public async ValueTask DisposeAsync()
		{
			if (Disposing is not null)
			{
				await Disposing();
			}

			DisposeCount++;
		}
	}
}

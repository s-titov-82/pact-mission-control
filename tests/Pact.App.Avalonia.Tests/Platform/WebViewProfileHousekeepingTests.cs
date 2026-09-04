using Avalonia.Controls;
using Pact.App.Avalonia.Platform;

namespace Pact.App.Avalonia.Tests.Platform;

public sealed class WebViewProfileHousekeepingTests
{
	[Test]
	public void Browser_request_clears_only_expired_disk_cache()
	{
		var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");

		var request = WebViewCleanupRequest.ForBrowser(now);

		request.DataKinds.ShouldBe(WebViewCleanupDataKinds.DiskCache);
		request.StartTime.ShouldBe(DateTimeOffset.UnixEpoch);
		request.EndTime.ShouldBe(now - TimeSpan.FromHours(72));
	}

	[Test]
	public void Terminal_request_clears_all_profile_data()
	{
		var request = WebViewCleanupRequest.ForTerminal();

		request.DataKinds.ShouldBe(WebViewCleanupDataKinds.AllProfile);
		request.StartTime.ShouldBeNull();
		request.EndTime.ShouldBeNull();
	}

	[Test]
	public async Task Each_profile_cleanup_runs_once_for_the_process_lifetime()
	{
		RecordingCleaner cleaner = new();
		WebViewProfileHousekeeping housekeeping = new(cleaner);

		await housekeeping.EnsureBrowserProfileAsync(new NativeWebView());
		await housekeeping.EnsureBrowserProfileAsync(new NativeWebView());
		await housekeeping.EnsureTerminalProfileAsync(new NativeWebView());
		await housekeeping.EnsureTerminalProfileAsync(new NativeWebView());

		cleaner.Requests.Count.ShouldBe(2);
		cleaner.Requests[0].Request.DataKinds.ShouldBe(WebViewCleanupDataKinds.DiskCache);
		cleaner.Requests[1].Request.DataKinds.ShouldBe(WebViewCleanupDataKinds.AllProfile);
	}

	[Test]
	public async Task Failed_cleanup_is_reported_once_and_is_not_retried()
	{
		var attempts = 0;
		List<Exception> failures = [];
		DelegateCleaner cleaner = new((_, _) =>
		{
			attempts++;
			throw new InvalidOperationException("cleanup failed");
		});
		WebViewProfileHousekeeping housekeeping = new(
			cleaner,
			reportFailureAsync: exception =>
			{
				failures.Add(exception);
				return Task.CompletedTask;
			});

		await housekeeping.EnsureBrowserProfileAsync(new NativeWebView());
		await housekeeping.EnsureBrowserProfileAsync(new NativeWebView());

		attempts.ShouldBe(1);
		failures.ShouldHaveSingleItem().Message.ShouldBe("cleanup failed");
	}

	private sealed class RecordingCleaner : IWebViewProfileDataCleaner
	{
		public List<(NativeWebView WebView, WebViewCleanupRequest Request)> Requests { get; } = [];

		public Task ClearAsync(NativeWebView webView, WebViewCleanupRequest request)
		{
			Requests.Add((webView, request));
			return Task.CompletedTask;
		}
	}

	private sealed class DelegateCleaner(
		Func<NativeWebView, WebViewCleanupRequest, Task> clearAsync) : IWebViewProfileDataCleaner
	{
		public Task ClearAsync(NativeWebView webView, WebViewCleanupRequest request) =>
			clearAsync(webView, request);
	}
}
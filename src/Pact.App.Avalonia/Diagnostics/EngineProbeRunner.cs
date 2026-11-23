using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Web;
using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Diagnostics;

/// <summary>
/// Captures evidence from the shipping preview's already-initialized controller and native hosts.
/// It never creates a second terminal backend: the selected saved session remains the terminal source.
/// </summary>
internal sealed class EngineProbeRunner
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private const string Candidate = "Avalonia.Controls.WebView 12.0.1 / Windows WebView2";
	private const string HostTransport = "chrome.webview";
	private const string ProbePage =
		"""
		<!doctype html>
		<html>
		<head><meta charset="utf-8"><title>Pact engine probe</title></head>
		<body>
		  <main data-build="42">
		    <span id="status">Running</span>
		    <span id="build">Build #123 complete</span>
		    <span id="background-tick">0</span>
		    <span id="visible-tick">0</span>
		    <span id="frame-tick">0</span>
		    <span id="monitor-phase">Idle</span>
		  </main>
		  <script>
		    let backgroundTick = 0;
		    let visibleTick = 0;
		    let frameTick = 0;
		    const phase = document.querySelector("#monitor-phase");
		    const countFrame = () => {
		      document.querySelector("#frame-tick").textContent = String(++frameTick);
		      if (frameTick === 1) phase.textContent = "Running";
		      // Keep the activity observable across the monitor's five-second poll interval.
		      if (frameTick === 80) phase.textContent = "Finished";
		      requestAnimationFrame(countFrame);
		    };
		    requestAnimationFrame(countFrame);
		    setInterval(() => {
		      document.querySelector("#background-tick").textContent =
		        String(++backgroundTick);
		      if (!document.hidden) {
		        document.querySelector("#visible-tick").textContent =
		          String(++visibleTick);
		      }
		    }, 100);
		  </script>
		</body>
		</html>
		""";

	internal static readonly string[] RequiredProbes = EngineProbeEvidenceEvaluator.RequiredProbes;

	private readonly string _outputPath;
	private readonly string _tempDirectory;

	private EngineProbeRunner(string outputPath, string tempDirectory)
	{
		_outputPath = outputPath;
		_tempDirectory = tempDirectory;
	}

	public static EngineProbeRunner? TryCreate(
		string? outputPath,
		AppDataProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		if (outputPath is null)
		{
			return null;
		}

		if (!Path.IsPathFullyQualified(outputPath))
		{
			throw new ArgumentException(
				"The engine probe requires an absolute JSON path.",
				nameof(outputPath));
		}

		var fullOutputPath = Path.GetFullPath(outputPath);
		var tempDirectory = new AppPaths(profile.RootDirectory).TempDirectory;
		if (!IsPathBelow(fullOutputPath, tempDirectory))
		{
			throw new ArgumentException(
				"The engine probe output must be below the selected data root's Temp directory.",
				nameof(outputPath));
		}

		return new EngineProbeRunner(fullOutputPath, tempDirectory);
	}

	public async Task<int> RunAsync(MainWindow window, Func<Task> shutdownAsync)
	{
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(shutdownAsync);

		ProductionEvidence? evidence = null;
		Exception? failure = null;
		try
		{
			evidence = await CollectProductionEvidenceAsync(window);
		}
		catch (Exception exception)
		{
			failure = exception;
		}

		var shutdownCompletedOnUiThread = false;
		try
		{
			var startedOnUiThread = Dispatcher.UIThread.CheckAccess();
			await shutdownAsync();
			shutdownCompletedOnUiThread = startedOnUiThread && Dispatcher.UIThread.CheckAccess();
		}
		catch (Exception exception)
		{
			failure = failure is null ? exception : new AggregateException(failure, exception);
		}

		var evaluation = EngineProbeEvidenceEvaluator.Evaluate(
			evidence?.TerminalDiagnostics ?? [],
			evidence?.BrowserDiagnostics ?? [],
			evidence?.RuntimeStarted == true,
			evidence?.RecentOutput ?? string.Empty,
			evidence?.SwitchCompleted == true,
			shutdownCompletedOnUiThread,
			evidence?.DomEvidence ?? new Dictionary<string, string?>(),
			evidence?.ProcessAttributionSucceeded == true);
		var allPassed = RequiredProbes.All(evaluation.Passed.Contains);

		await WriteJsonAtomicallyAsync(_outputPath, new
		{
			candidate = Candidate,
			hostTransport = HostTransport,
			required = RequiredProbes,
			passed = evaluation.Passed,
			decision = evaluation.Decision,
			runtimeSessionId = evidence?.RuntimeSessionId,
			recentOutput = evidence?.RecentOutput,
			terminalDiagnostics = evidence?.TerminalDiagnostics ?? [],
			browserDiagnostics = evidence?.BrowserDiagnostics ?? [],
			controllerDiagnostics = evidence?.ControllerDiagnostics ?? [],
			domEvidence = evidence?.DomEvidence ?? new Dictionary<string, string?>(),
			webProcessAttribution = evidence?.WebProcessAttribution,
			error = failure?.ToString()
		});
		return allPassed && failure is null ? 0 : 3;
	}

	private async Task<ProductionEvidence> CollectProductionEvidenceAsync(MainWindow window)
	{
		var controller = window.EngineProbeController;
		var terminalHost = window.EngineProbeTerminalHost;
		var session = await EnsureProbeSessionAsync(controller.ViewModel);

		await controller.SelectSessionAsync(
			session,
			startIfNeeded: true,
			cancellationToken: CancellationToken.None);
		controller.Runtimes.TryGetValue(session.Record.Id, out var runtime);
		var runtimeStarted = false;
		if (runtime?.TryGetController(out var runtimeController) == true
			&& runtimeController.IsActive)
		{
			runtimeStarted = true;
			if (!await WaitUntilAsync(
				() => terminalHost.DiagnosticSnapshot.Any(entry =>
					entry.Phase == "webmessage-handled"
					&& entry.Detail == "type=input"),
				TimeSpan.FromSeconds(5)))
			{
				throw new TimeoutException(
					"The production terminal did not return its ConPTY handshake input within 5 seconds.");
			}

			if (!await runtimeController.WriteInputAsync(
					"Write-Output FINAL_ENGINE_PROBE_READY\r"))
			{
				throw new InvalidOperationException(
					"The production probe could not write its terminal output marker.");
			}

			if (!await WaitUntilAsync(
				() => HasVisibleText(runtime.GetRecentOutput()),
				TimeSpan.FromSeconds(15)))
			{
				throw new TimeoutException(
					"The production terminal did not return visible output within 15 seconds.");
			}
		}

		var recentOutput = runtime?.GetRecentOutput() ?? string.Empty;
		await VerifyStyledSnapshotsAsync(terminalHost, session.Record.Id);
		var captureMilliseconds = await terminalHost.MeasureSnapshotCaptureAsync(session.Record.Id);
		var workspace = controller.ViewModel.SelectedWorkspace
			?? controller.ViewModel.Workspaces.FirstOrDefault()
			?? throw new InvalidOperationException("The production probe requires a project in the temporary profile.");
		var localPagePath = Path.Combine(
			_tempDirectory,
			$"engine-probe-page-{Guid.NewGuid():N}.html");
		WebViewDiagnosticEntry[] browserDiagnostics = [];
		var switchCompleted = false;
		IReadOnlyDictionary<string, string?> domEvidence = new Dictionary<string, string?>();
		WebViewProcessAttribution? webProcessAttribution = null;
		WebPageViewModel? probePage = null;
		try
		{
			Directory.CreateDirectory(_tempDirectory);
			await File.WriteAllTextAsync(localPagePath, ProbePage);
			Uri localPage = new(localPagePath);
			probePage = await controller.ViewModel.CreateWebPageAsync(
				$"engine-probe-{Guid.NewGuid():N}",
				workspace.Id,
				"Engine probe",
				localPage.AbsoluteUri,
				CancellationToken.None);
			await controller.ResumeMonitoredPageForDiagnosticsAsync(probePage, new WebMonitorRule(
				"engine-probe", "Engine probe", Enabled: true,
				"^" + Regex.Escape(localPage.AbsoluteUri) + "$",
				WebMonitorRuleCompiler.MinimumPollIntervalSeconds,
				new("#monitor-phase", WebMonitorValueSource.Text, null, "^Running$", null),
				new("#monitor-phase", WebMonitorValueSource.Text, null, null, null)));
			var hiddenHost = await WaitForBrowserDocumentAsync(controller, probePage);
			await WaitForMonitorStatusAsync(probePage, WebMonitorStatus.Activity);
			domEvidence = await CollectDomEvidenceAsync(hiddenHost);
			await WaitForMonitorStatusAsync(probePage, WebMonitorStatus.Unread);
			if (window.IsBrowserPaneVisible || hiddenHost.DiagnosticSnapshot.Any(entry => entry.Phase == "shown"))
			{
				throw new InvalidOperationException("The background probe page was presented before evidence was collected.");
			}
			domEvidence = new Dictionary<string, string?>(domEvidence, StringComparer.Ordinal)
			{
				["styled-screen"] = "passed",
				["styled-capture-ms-200x50"] = captureMilliseconds
			};
			// Use a fresh page to also verify first render for a foreground open.
			await controller.CloseWebPageAsync(probePage, CancellationToken.None);
			probePage = await controller.ViewModel.CreateWebPageAsync(
				$"engine-probe-{Guid.NewGuid():N}",
				workspace.Id,
				"Engine probe",
				localPage.AbsoluteUri,
				CancellationToken.None);
			await controller.SelectWebPageAsync(probePage, CancellationToken.None);
			var browserHost = await WaitForBrowserDocumentAsync(controller, probePage);
			webProcessAttribution = await browserHost.ReadProcessAttributionAsync(
				CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
			await CheckHttpErrorsAsync(window, browserHost);
			await controller.SelectSessionAsync(
				session,
				startIfNeeded: true,
				cancellationToken: CancellationToken.None);
			switchCompleted = controller.IsTerminalVisible
				&& controller.Runtimes.TryGetValue(session.Record.Id, out var activeRuntime)
				&& activeRuntime.TryGetController(out var activeController)
				&& activeController.IsActive;
			browserDiagnostics = browserHost.DiagnosticSnapshot;
		}
		finally
		{
			if (probePage is not null)
			{
				await controller.CloseWebPageAsync(probePage, CancellationToken.None);
			}

			File.Delete(localPagePath);
		}

		return new ProductionEvidence(
			session.Record.Id,
			runtimeStarted,
			recentOutput,
			switchCompleted,
			terminalHost.DiagnosticSnapshot,
			browserDiagnostics,
			controller.DiagnosticSnapshot,
			domEvidence,
			webProcessAttribution);
	}

	private static async Task VerifyStyledSnapshotsAsync(AvaloniaTerminalWebViewHost host, string sessionId)
	{
		const string marker = "PACT_DIM_😀e\u0301界";
		try
		{
			await host.WriteOutputAsync(sessionId, "\u001b[?1049h\u001b[2J");
			foreach (var dim in new[] { true, false })
			{
				TaskCompletionSource<TerminalScreenSnapshot> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
				var observations = 0;
				var matchedRows = 0;
				var capturedRows = 0;
				void OnSnapshot(object? sender, (string SessionId, TerminalScreenSnapshot Snapshot) observation)
				{
					if (observation.SessionId != sessionId || !observation.Snapshot.Stable)
					{
						return;
					}
					observations++;
					capturedRows = observation.Snapshot.Rows.Count(row => row.DimRanges is not null);
					foreach (var row in observation.Snapshot.Rows)
					{
						var offset = row.Text.IndexOf(marker, StringComparison.Ordinal);
						if (offset >= 0)
						{
							matchedRows++;
						}
						if (offset < 0 || row.DimRanges is not { } ranges)
						{
							continue;
						}
						var markerDim = ranges.Any(range => range.Start <= offset && range.Start + range.Length >= offset + marker.Length);
						if (markerDim == dim)
						{
							received.TrySetResult(observation.Snapshot);
						}
					}
				}
				host.ScreenSnapshotReceived += OnSnapshot;
				try
				{
					await host.WriteOutputAsync(sessionId, "\u001b[999;1H\u001b[2K" + (dim ? "\u001b[2m" : "\u001b[22m") + marker + "\u001b[0m");
					await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
				}
				catch (TimeoutException exception)
				{
					var styling = host.DiagnosticSnapshot.LastOrDefault(entry => entry.Phase == "snapshot-styling")?.Detail;
					throw new TimeoutException($"Styled snapshot dim={dim}: observations={observations}, markerRows={matchedRows}, capturedRows={capturedRows}, styling={styling}.", exception);
				}
				finally
				{
					host.ScreenSnapshotReceived -= OnSnapshot;
				}
			}
		}
		finally
		{
			await host.WriteOutputAsync(sessionId, "\u001b[?1049l");
		}
	}

	private async Task<SessionViewModel> EnsureProbeSessionAsync(MainWindowViewModel viewModel)
	{
		var session = viewModel.SelectedSession ?? viewModel.Sessions.FirstOrDefault();
		if (session is not null)
		{
			return session;
		}

		var workspace = viewModel.SelectedWorkspace ?? viewModel.Workspaces.FirstOrDefault();
		if (workspace is null)
		{
			var workspacePath = Path.Combine(_tempDirectory, "EngineProbeWorkspace");
			Directory.CreateDirectory(workspacePath);
			workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
				workspacePath,
				CancellationToken.None);
		}

		return await viewModel.CreateSessionAsync(
			workspace.Id,
			AgentKind.Pwsh,
			"Engine probe",
			workspace.RootPath,
			"pwsh.exe -NoLogo -NoProfile",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
	}

	private static async Task CheckHttpErrorsAsync(MainWindow window, AvaloniaWebPageHost host)
	{
		const string unstyled = "<html><body><h1>403 Forbidden</h1>\nRequest forbidden by administrative rules.\n</body></html>";
		foreach (var (status, type, body) in new[] { (200, "text/html", unstyled), (403, "text/html", unstyled),
			(403, "text/html", ""), (403, "application/json", "{\"error\":\"denied\"}"),
			(403, "text/plain", "Access denied"), (403, "text/html", "<body style='background:black'><input type='hidden'></body>"),
			(403, "text/html", "<html><body style='background:black;color:black'><p id='denied'>Access denied</p></body></html>") })
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
			using var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/denied");
			var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			void Failed(object? sender, string message)
			{
				completed.TrySetResult();
			}
			host.NavigationFailed += Failed;
			void Navigated(object? sender, EventArgs args)
			{
				completed.TrySetResult();
			}
			if (status == 200)
			{
				host.NavigationCompleted += Navigated;
			}
			try
			{
				await host.HideAsync(timeout.Token);
				await host.NavigateAsync(url, timeout.Token);
				using var client = await listener.AcceptTcpClientAsync(timeout.Token);
				using var stream = client.GetStream();
				using var reader = new StreamReader(stream);
				while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 })
				{
					// Consume the request through the empty line ending its headers.
				}
				var bytes = Encoding.UTF8.GetBytes(body);
				var reason = status == 200 ? "OK" : "Forbidden";
				var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
				await stream.WriteAsync(headers, timeout.Token);
				await stream.WriteAsync(bytes, timeout.Token);
				client.Client.Shutdown(SocketShutdown.Send);
				await completed.Task.WaitAsync(timeout.Token);
				await host.ShowAsync(timeout.Token);
				window.UpdateLayout();
				var pane = window.GetVisualDescendants().OfType<BrowserPaneView>().Single();
				var visibleBrowser = pane.GetVisualDescendants().OfType<NativeWebView>().SingleOrDefault(view => view.IsEffectivelyVisible);
				var heading = pane.GetVisualDescendants().OfType<TextBlock>().SingleOrDefault(label =>
					label.IsEffectivelyVisible && label.Text == "Ошибка загрузки: 403");
				if (body == unstyled)
				{
					if (visibleBrowser is null || heading is not null || await EvaluateRevisionAsync(host, new("h1", WebMonitorValueSource.Text, null, null, null)) != "403 Forbidden")
					{
						throw new InvalidOperationException($"Unstyled HTTP {status} document missing.");
					}
					await CheckDocumentColorsAsync(visibleBrowser, status);
					continue;
				}
				if (body.StartsWith("<html>", StringComparison.Ordinal))
				{
					if (visibleBrowser is null || heading is not null ||
						await EvaluateRevisionAsync(host, new("#denied", WebMonitorValueSource.Text, null, null, null)) != "Access denied")
					{
						throw new InvalidOperationException("HTTP 403 server HTML missing.");
					}
					continue;
				}
				var labels = window.GetVisualDescendants().OfType<TextBlock>().Where(label => label.IsEffectivelyVisible)
					.Select(label => label.Text?.ReplaceLineEndings("\n")).ToArray();
				if (visibleBrowser is not null || heading is null || (type != "text/html" &&
					!labels.Contains(type == "application/json" ? "{\n  \"error\": \"denied\"\n}" : body)))
				{
					var diagnostics = string.Join(", ", host.DiagnosticSnapshot.TakeLast(8).Select(entry => $"{entry.Phase} {entry.Detail}"));
					throw new InvalidOperationException($"HTTP 403 error surface missing for {type} ({bytes.Length} bytes). {diagnostics}");
				}
			}
			finally
			{
				host.NavigationFailed -= Failed;
				host.NavigationCompleted -= Navigated;
			}
		}
	}

	private static async Task CheckDocumentColorsAsync(NativeWebView view, int status)
	{
		var browser = WindowsWebView2BackgroundVisibility.TryGetCore(view)!;
		using MemoryStream image = new();
		await browser.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
		image.Position = 0;
		using var bitmap = SkiaSharp.SKBitmap.Decode(image);
		var paper = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);
		var darkPixels = 0;
		for (var y = 0; y < Math.Min(160, bitmap.Height); y++)
		{
			for (var x = 0; x < Math.Min(480, bitmap.Width); x++)
			{
				var pixel = bitmap.GetPixel(x, y);
				if (pixel is { Alpha: 255, Red: < 50, Green: < 50, Blue: < 50 })
				{
					darkPixels++;
				}
			}
		}
		if (paper is not { Alpha: 255, Red: > 230, Green: > 230, Blue: > 230 } || darkPixels < 20)
		{
			throw new InvalidOperationException($"Unstyled HTTP {status}: canvas={paper}, dark pixels={darkPixels}.");
		}
	}

	private static async Task<AvaloniaWebPageHost> WaitForBrowserDocumentAsync(
		Controllers.AvaloniaMainShellController controller,
		WebPageViewModel page)
	{
		var browserHost = controller.GetWebPageHostForDiagnostics(page.Record.Id) as AvaloniaWebPageHost
			?? throw new InvalidOperationException("The production controller did not create an Avalonia browser host.");
		if (!await WaitUntilAsync(
			() => browserHost.DiagnosticSnapshot.Any(entry =>
				entry.Phase is "document-response" or "navigation-failed"),
			TimeSpan.FromSeconds(15)))
		{
			throw new TimeoutException("The production browser did not complete navigation within 15 seconds.");
		}
		if (browserHost.DiagnosticSnapshot.Any(entry => entry.Phase == "navigation-failed"))
		{
			throw new InvalidOperationException("The production browser failed to navigate to the local probe page.");
		}
		return browserHost;
	}

	private static async Task WaitForMonitorStatusAsync(WebPageViewModel page, WebMonitorStatus expected)
	{
		if (!await WaitUntilAsync(() => page.MonitorStatus == expected, TimeSpan.FromSeconds(15)))
		{
			throw new TimeoutException($"Background monitoring did not reach {expected}; last status was {page.MonitorStatus}.");
		}
	}

	private static async Task<IReadOnlyDictionary<string, string?>> CollectDomEvidenceAsync(
		AvaloniaWebPageHost browserHost)
	{
		Dictionary<string, string?> evidence = new(StringComparer.Ordinal)
		{
			["dom-text"] = await EvaluateRevisionAsync(
				browserHost,
				new WebMonitorExtractor(
					"#status",
					WebMonitorValueSource.Text,
					AttributeName: null,
					MatchPattern: null,
					CaptureGroup: null)),
			["dom-attribute"] = await EvaluateRevisionAsync(
				browserHost,
				new WebMonitorExtractor(
					"main",
					WebMonitorValueSource.Attribute,
					"data-build",
					MatchPattern: null,
					CaptureGroup: null)),
			["dom-regex"] = await EvaluateRevisionAsync(
				browserHost,
				new WebMonitorExtractor(
					"#build",
					WebMonitorValueSource.Text,
					AttributeName: null,
					@"Build #(\d+)",
					CaptureGroup: 1)),
			["dom-missing"] = await EvaluateRevisionAsync(
				browserHost,
				new WebMonitorExtractor(
					".missing",
					WebMonitorValueSource.Text,
					AttributeName: null,
					MatchPattern: null,
					CaptureGroup: null)),
		};

		// The page must run its first animation frame without ever being presented, as when
		// Pact restores a monitored browser tab behind a terminal at startup.
		(string Probe, string Selector, int MinimumAdvance)[] counters =
		[
			("background-timer", "#background-tick", 10),
			("background-visibility", "#visible-tick", 10),
			("background-frames", "#frame-tick", 5),
		];
		var before = new int?[counters.Length];
		for (var index = 0; index < counters.Length; index++)
		{
			before[index] = await ReadCounterAsync(browserHost, counters[index].Selector);
		}

		await Task.Delay(TimeSpan.FromSeconds(3));
		for (var index = 0; index < counters.Length; index++)
		{
			var after = await ReadCounterAsync(browserHost, counters[index].Selector);
			evidence[counters[index].Probe] = before[index] is int first
				&& after is int second
				&& second - first >= counters[index].MinimumAdvance
					? "active"
					: $"inactive:{before[index]}->{after}";
		}

		return evidence;
	}

	private static async Task<int?> ReadCounterAsync(AvaloniaWebPageHost browserHost, string selector)
	{
		var value = await EvaluateRevisionAsync(
			browserHost,
			new WebMonitorExtractor(
				selector,
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null));
		return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var counter)
			? counter
			: null;
	}

	private static async Task<string?> EvaluateRevisionAsync(
		AvaloniaWebPageHost browserHost,
		WebMonitorExtractor revision)
	{
		var evaluation = await browserHost.EvaluateMonitorAsync(
			new WebMonitorDomQuery(
				Activity: null,
				revision,
				ActivityWhenExtractorMissing: false),
			CancellationToken.None);
		return evaluation.Observation?.Revision;
	}

	private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
	{
		if (condition())
		{
			return true;
		}

		using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(50));
		using CancellationTokenSource cancellation = new(timeout);
		try
		{
			while (await timer.WaitForNextTickAsync(cancellation.Token))
			{
				if (condition())
				{
					return true;
				}
			}
		}
		catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
		{
		}
		return false;
	}

	internal static bool HasVisibleText(string text) =>
		EngineProbeEvidenceEvaluator.HasCleanTerminalOutput(text);

	private static bool IsPathBelow(string candidate, string parent)
	{
		var relative = Path.GetRelativePath(parent, candidate);
		return !Path.IsPathFullyQualified(relative)
			&& !string.Equals(relative, ".", StringComparison.Ordinal)
			&& !string.Equals(relative, "..", StringComparison.Ordinal)
			&& !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
			&& !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
	}

	private static async Task WriteJsonAtomicallyAsync(string path, object payload)
	{
		var directory = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var temporaryPath = Path.Combine(
			directory ?? string.Empty,
			$".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try
		{
			var json = JsonSerializer.Serialize(payload, JsonOptions);
			await File.WriteAllTextAsync(temporaryPath, json);
			File.Move(temporaryPath, path, overwrite: true);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	private sealed record ProductionEvidence(
		string RuntimeSessionId,
		bool RuntimeStarted,
		string RecentOutput,
		bool SwitchCompleted,
		WebViewDiagnosticEntry[] TerminalDiagnostics,
		WebViewDiagnosticEntry[] BrowserDiagnostics,
		WebViewDiagnosticEntry[] ControllerDiagnostics,
		IReadOnlyDictionary<string, string?> DomEvidence,
		WebViewProcessAttribution? WebProcessAttribution)
	{
		internal bool ProcessAttributionSucceeded =>
			WebProcessAttribution?.HasExactPageAttribution == true;
	}
}

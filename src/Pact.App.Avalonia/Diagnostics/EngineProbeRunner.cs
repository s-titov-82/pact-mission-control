using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Web;
using Pact.Core.Agents;
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
	private const string OutputArgument = "--engine-probe-output";
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
		  </main>
		  <script>
		    let backgroundTick = 0;
		    setInterval(() => {
		      document.querySelector("#background-tick").textContent =
		        String(++backgroundTick);
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
		IReadOnlyList<string> args,
		AppDataProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		string? outputPath = null;
		for (var index = 0; index < args.Count; index++)
		{
			if (!string.Equals(args[index], OutputArgument, StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (outputPath is not null)
			{
				throw new ArgumentException($"{OutputArgument} may be specified only once.", nameof(args));
			}

			if (++index >= args.Count || string.IsNullOrWhiteSpace(args[index]))
			{
				throw new ArgumentException($"{OutputArgument} requires an absolute JSON path.", nameof(args));
			}

			outputPath = args[index];
		}

		if (outputPath is null)
		{
			return null;
		}

		if (!Path.IsPathFullyQualified(outputPath))
		{
			throw new ArgumentException($"{OutputArgument} requires an absolute JSON path.", nameof(args));
		}

		var fullOutputPath = Path.GetFullPath(outputPath);
		var tempDirectory = new AppPaths(profile.RootDirectory).TempDirectory;
		if (!IsPathBelow(fullOutputPath, tempDirectory))
		{
			throw new ArgumentException(
				$"{OutputArgument} must be below the selected data root's Temp directory.",
				nameof(args));
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
			await controller.SelectWebPageAsync(probePage, CancellationToken.None);
			var portableBrowserHost = controller.GetWebPageHostForDiagnostics(probePage.Record.Id);
			var browserHost = portableBrowserHost as AvaloniaWebPageHost
				?? throw new InvalidOperationException("The production controller did not create an Avalonia browser host.");

			if (!await WaitUntilAsync(
				() => browserHost.DiagnosticSnapshot.Any(entry =>
					entry.Phase is "document-response" or "navigation-failed"),
				TimeSpan.FromSeconds(15)))
			{
				throw new TimeoutException(
					"The production browser did not complete navigation within 15 seconds.");
			}

			if (browserHost.DiagnosticSnapshot.Any(entry => entry.Phase == "navigation-failed"))
			{
				throw new InvalidOperationException(
					"The production browser failed to navigate to the local probe page.");
			}

			domEvidence = await CollectDomEvidenceAsync(browserHost);
			webProcessAttribution = await browserHost.ReadProcessAttributionAsync(
				CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
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

		var timerExtractor = new WebMonitorExtractor(
			"#background-tick",
			WebMonitorValueSource.Text,
			AttributeName: null,
			MatchPattern: null,
			CaptureGroup: null);
		var firstTimerValue = await EvaluateRevisionAsync(browserHost, timerExtractor);
		await browserHost.HideAsync(CancellationToken.None);
		await Task.Delay(TimeSpan.FromSeconds(3));
		var secondTimerValue = await EvaluateRevisionAsync(browserHost, timerExtractor);
		if (int.TryParse(firstTimerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var first)
			&& int.TryParse(secondTimerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var second)
			&& second - first >= 10)
		{
			evidence["background-timer"] = "active";
		}

		return evidence;
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

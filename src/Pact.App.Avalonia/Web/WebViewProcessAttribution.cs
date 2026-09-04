namespace Pact.App.Avalonia.Web;

internal enum WebViewRuntimeProcessKind
{
	Browser,
	Renderer,
	Utility,
	SandboxHelper,
	Gpu,
	Plugin,
	PluginBroker
}

internal sealed record WebViewRuntimeProcessInfo(
	int ProcessId,
	WebViewRuntimeProcessKind Kind,
	IReadOnlyList<uint> AssociatedRootFrameIds);

internal sealed record WebViewProcessAttribution(
	IReadOnlyList<int> PageProcessIds,
	IReadOnlyList<int> SharedProcessIds,
	bool PageAttributionAvailable = true,
	int? RuntimeRootProcessId = null)
{
	internal bool HasExactPageAttribution =>
		PageAttributionAvailable
		&& PageProcessIds.Count > 0
		&& SharedProcessIds.Count > 0;
}

internal interface IWebPageProcessAttributionSource
{
	Task<WebViewProcessAttribution> ReadProcessAttributionAsync(
		CancellationToken cancellationToken);
}

internal static class WebViewProcessAttributionClassifier
{
	internal static WebViewProcessAttribution Classify(
		uint selectedFrameId,
		IEnumerable<WebViewRuntimeProcessInfo> processes)
	{
		ArgumentOutOfRangeException.ThrowIfZero(selectedFrameId);
		ArgumentNullException.ThrowIfNull(processes);
		List<int> pageProcessIds = [];
		List<int> sharedProcessIds = [];

		foreach (var process in processes)
		{
			if (process.Kind != WebViewRuntimeProcessKind.Renderer)
			{
				sharedProcessIds.Add(process.ProcessId);
				continue;
			}

			var rootFrameIds = process.AssociatedRootFrameIds.Distinct().ToArray();
			if (!rootFrameIds.Contains(selectedFrameId))
			{
				continue;
			}

			if (rootFrameIds.Length == 1)
			{
				pageProcessIds.Add(process.ProcessId);
			}
			else
			{
				sharedProcessIds.Add(process.ProcessId);
			}
		}

		return new(pageProcessIds, sharedProcessIds);
	}
}

namespace Pact.Presentation.ViewModels;

/// <summary>Resource metrics for one attributed process group.</summary>
/// <param name="ProcessCount">Number of processes in the group.</param>
/// <param name="WorkingSetBytes">Combined readable working sets, in bytes.</param>
/// <param name="CpuPercent">Machine-normalized CPU load, or null until two samples exist.</param>
public sealed record ProcessMetricsGroupViewModel(
	int ProcessCount,
	long WorkingSetBytes,
	double? CpuPercent);

/// <summary>Runtime-only WebView2 resource metrics for the selected loaded web tab.</summary>
/// <param name="PageRenderers">Renderer processes attributable only to the selected page.</param>
/// <param name="SharedRuntime">Browser, GPU, utility, and multi-page renderer processes.</param>
/// <param name="SampledAt">UTC time of the latest process snapshot.</param>
/// <param name="Error">Failure description when the latest snapshot was unavailable.</param>
/// <param name="PageAttributionAvailable">
/// Whether the installed WebView2 Runtime can separate the selected page from shared processes.
/// </param>
public sealed record WebViewProcessMetricsViewModel(
	ProcessMetricsGroupViewModel PageRenderers,
	ProcessMetricsGroupViewModel SharedRuntime,
	DateTimeOffset SampledAt,
	string? Error = null,
	bool PageAttributionAvailable = true)
{
	/// <summary>Whether the latest WebView2 process snapshot was read successfully.</summary>
	public bool IsAvailable => string.IsNullOrWhiteSpace(Error);
}

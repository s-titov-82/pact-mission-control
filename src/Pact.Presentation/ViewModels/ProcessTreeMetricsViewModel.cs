namespace Pact.Presentation.ViewModels;

/// <summary>Runtime-only resource metrics for the selected terminal process tree.</summary>
/// <param name="RootProcessId">Process id used as the tree root.</param>
/// <param name="ProcessCount">Number of processes found in the tree.</param>
/// <param name="WorkingSetBytes">Combined readable working sets, in bytes.</param>
/// <param name="CpuPercent">Machine-normalized CPU load, or null until two samples exist.</param>
/// <param name="SampledAt">UTC time of the latest operating-system snapshot.</param>
/// <param name="Error">Failure description when the latest snapshot was unavailable.</param>
public sealed record ProcessTreeMetricsViewModel(
	int RootProcessId,
	int ProcessCount,
	long WorkingSetBytes,
	double? CpuPercent,
	DateTimeOffset SampledAt,
	string? Error = null)
{
	/// <summary>Whether the latest process-tree snapshot was read successfully.</summary>
	public bool IsAvailable => string.IsNullOrWhiteSpace(Error);
}

using Pact.App.Avalonia.Web;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Controllers;

internal sealed class WebProcessMetricsSnapshotReader(
	Func<string, IWebPageProcessAttributionSource?> sourceResolver,
	IProcessSetSnapshotReader processReader,
	IProcessTreeSnapshotReader? processTreeReader = null) : IWebProcessMetricsSnapshotReader
{
	private readonly Func<string, IWebPageProcessAttributionSource?> _sourceResolver =
		sourceResolver ?? throw new ArgumentNullException(nameof(sourceResolver));
	private readonly IProcessSetSnapshotReader _processReader =
		processReader ?? throw new ArgumentNullException(nameof(processReader));
	private readonly IProcessTreeSnapshotReader? _processTreeReader = processTreeReader;

	public async Task<WebProcessMetricsSnapshot> ReadAsync(
		string pageId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(pageId);
		cancellationToken.ThrowIfCancellationRequested();
		var source = _sourceResolver(pageId)
			?? throw new InvalidOperationException("The selected web tab is not loaded.");
		var attribution = await source.ReadProcessAttributionAsync(cancellationToken)
			.ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
		var pageSnapshot = _processReader.Read(attribution.PageProcessIds);
		var sharedSnapshot = attribution.RuntimeRootProcessId is int runtimeRootProcessId
			? ReadTree(runtimeRootProcessId)
			: _processReader.Read(attribution.SharedProcessIds);
		return new(pageSnapshot, sharedSnapshot, attribution.PageAttributionAvailable);
	}

	private ProcessSetSnapshot ReadTree(int rootProcessId)
	{
		var treeReader = _processTreeReader
			?? throw new InvalidOperationException(
				"Aggregate WebView2 metrics require a process-tree reader.");
		var snapshot = treeReader.Read(rootProcessId);
		return new(
			snapshot.ProcessCount,
			snapshot.WorkingSetBytes,
			snapshot.ProcessorTimes,
			snapshot.SampledAt);
	}
}

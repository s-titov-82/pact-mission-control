using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Web;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class WebProcessMetricsSnapshotReaderTests
{
	[Test]
	public async Task Loaded_page_processes_are_read_as_separate_page_and_shared_groups()
	{
		FakeProcessSetSnapshotReader processReader = new();
		FakeAttributionSource source = new(new([20, 21], [100, 101, 102]));
		WebProcessMetricsSnapshotReader reader = new(
			pageId => pageId == "web-1" ? source : null,
			processReader);

		var snapshot = await reader.ReadAsync("web-1", CancellationToken.None);

		processReader.ProcessIdSets.ShouldBe([[20, 21], [100, 101, 102]]);
		snapshot.PageRenderers.ProcessCount.ShouldBe(2);
		snapshot.SharedRuntime.ProcessCount.ShouldBe(3);
	}

	[Test]
	public async Task Aggregate_runtime_fallback_is_preserved_without_claiming_page_attribution()
	{
		FakeProcessSetSnapshotReader processReader = new();
		FakeProcessTreeSnapshotReader treeReader = new();
		FakeAttributionSource source = new(new(
			PageProcessIds: [],
			SharedProcessIds: [100],
			PageAttributionAvailable: false,
			RuntimeRootProcessId: 100));
		WebProcessMetricsSnapshotReader reader = new(_ => source, processReader, treeReader);

		var snapshot = await reader.ReadAsync("web-1", CancellationToken.None);

		snapshot.PageAttributionAvailable.ShouldBeFalse();
		snapshot.SharedRuntime.ProcessCount.ShouldBe(3);
		processReader.ProcessIdSets.ShouldBe([[]]);
		treeReader.RootProcessIds.ShouldBe([100]);
	}

	[Test]
	public async Task Unloaded_page_is_rejected_without_reading_processes()
	{
		FakeProcessSetSnapshotReader processReader = new();
		WebProcessMetricsSnapshotReader reader = new(_ => null, processReader);

		var action = () => reader.ReadAsync("paused-web", CancellationToken.None);

		var exception = await action.ShouldThrowAsync<InvalidOperationException>();
		exception.Message.ShouldBe("The selected web tab is not loaded.");
		processReader.ProcessIdSets.ShouldBeEmpty();
	}

	private sealed class FakeAttributionSource(WebViewProcessAttribution attribution)
		: IWebPageProcessAttributionSource
	{
		public Task<WebViewProcessAttribution> ReadProcessAttributionAsync(
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(attribution);
		}
	}

	private sealed class FakeProcessSetSnapshotReader : IProcessSetSnapshotReader
	{
		internal List<int[]> ProcessIdSets { get; } = [];

		public ProcessSetSnapshot Read(IEnumerable<int> processIds)
		{
			var ids = processIds.ToArray();
			ProcessIdSets.Add(ids);
			return new(
				ids.Length,
				WorkingSetBytes: ids.Length * 1024,
				ids.ToDictionary(id => id, _ => TimeSpan.Zero),
				DateTimeOffset.UnixEpoch);
		}
	}

	private sealed class FakeProcessTreeSnapshotReader : IProcessTreeSnapshotReader
	{
		internal List<int> RootProcessIds { get; } = [];

		public ProcessTreeSnapshot Read(int rootProcessId)
		{
			RootProcessIds.Add(rootProcessId);
			return new(
				rootProcessId,
				ProcessCount: 3,
				WorkingSetBytes: 4096,
				new Dictionary<int, TimeSpan> { [rootProcessId] = TimeSpan.Zero },
				DateTimeOffset.UnixEpoch);
		}
	}
}

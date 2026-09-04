using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;

namespace Pact.App.Avalonia.Controllers;

internal interface IWebMonitorSnapshotReader
{
	Task SweepAsync(
		IReadOnlySet<string> existingWebPageIds,
		CancellationToken cancellationToken);

	Task<WebMonitorSnapshot?> LoadAsync(
		string webPageId,
		CancellationToken cancellationToken);
}

internal sealed class WebMonitorSnapshotReader : IWebMonitorSnapshotReader
{
	private readonly WebMonitorSnapshotStore _store;

	public WebMonitorSnapshotReader(WebMonitorSnapshotStore store)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
	}

	public Task SweepAsync(
		IReadOnlySet<string> existingWebPageIds,
		CancellationToken cancellationToken) =>
		_store.SweepAsync(existingWebPageIds, cancellationToken);

	public Task<WebMonitorSnapshot?> LoadAsync(
		string webPageId,
		CancellationToken cancellationToken) =>
		_store.LoadAsync(webPageId, cancellationToken);
}
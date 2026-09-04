using Pact.Core.RootTabs;

namespace Pact.Presentation.Services;

/// <summary>
/// Process-local empty ROOT store used only by presentation hosts that have not supplied durable
/// storage, primarily narrow tests and previews.
/// </summary>
internal sealed class VolatileRootTabsStore : IRootTabsStore
{
	private readonly Lock _gate = new();
	private RootTabsRecord _record = RootTabsRecord.CreateDefault();

	public Task<RootTabsRecord> LoadAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			return Task.FromResult(_record);
		}
	}

	public Task SaveAsync(RootTabsRecord document, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(document);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			_record = document.Normalize();
			return Task.CompletedTask;
		}
	}

	public Task<RootTabsRecord> UpdateAsync(
		Func<RootTabsRecord, RootTabsRecord> update,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(update);
		cancellationToken.ThrowIfCancellationRequested();
		lock (_gate)
		{
			_record = update(_record).Normalize();
			return Task.FromResult(_record);
		}
	}
}

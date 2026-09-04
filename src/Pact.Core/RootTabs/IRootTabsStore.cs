namespace Pact.Core.RootTabs;

/// <summary>Atomic persistence for <c>Settings/root-tabs.json</c>.</summary>
public interface IRootTabsStore
{
	/// <summary>Loads and normalizes the current root-tab document.</summary>
	Task<RootTabsRecord> LoadAsync(CancellationToken cancellationToken);

	/// <summary>Writes the complete document atomically.</summary>
	Task SaveAsync(RootTabsRecord document, CancellationToken cancellationToken);

	/// <summary>
	/// Applies one serialized read-modify-write mutation and returns the persisted document.
	/// </summary>
	Task<RootTabsRecord> UpdateAsync(
		Func<RootTabsRecord, RootTabsRecord> update,
		CancellationToken cancellationToken);
}

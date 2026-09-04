using Pact.Core.Sessions;
using Pact.Core.Web;

namespace Pact.Core.RootTabs;

/// <summary>
/// Persistent project-independent terminal and browser tabs stored in
/// <c>Settings/root-tabs.json</c>.
/// </summary>
/// <param name="SchemaVersion">Document format version.</param>
/// <param name="ActiveItemId">Last selected root item, or <see langword="null"/>.</param>
/// <param name="Sessions">Saved root terminal sessions.</param>
/// <param name="WebPages">Saved root browser tabs.</param>
/// <param name="PausedItemIds">Items explicitly paused by the user.</param>
public sealed record RootTabsRecord(
	int SchemaVersion,
	string? ActiveItemId,
	IReadOnlyList<SessionRecord> Sessions,
	IReadOnlyList<WebPageRecord> WebPages,
	IReadOnlyList<string> PausedItemIds)
{
	/// <summary>Creates the empty first-run document.</summary>
	public static RootTabsRecord CreateDefault() => new(
		SchemaVersion: 1,
		ActiveItemId: null,
		Sessions: [],
		WebPages: [],
		PausedItemIds: []);

	/// <summary>
	/// Validates item identity and removes selection or pause references to missing items.
	/// </summary>
	/// <exception cref="InvalidDataException">
	/// Thrown when terminal and browser collections contain the same stable item id.
	/// </exception>
	public RootTabsRecord Normalize()
	{
		var itemIds = new HashSet<string>(StringComparer.Ordinal);
		foreach (var itemId in Sessions.Select(session => session.Id)
					 .Concat(WebPages.Select(webPage => webPage.Id)))
		{
			if (string.IsNullOrWhiteSpace(itemId) || !itemIds.Add(itemId))
			{
				throw new InvalidDataException(
					$"Root tab id '{itemId}' is empty or duplicated.");
			}
		}

		var pausedItemIds = PausedItemIds
			.Where(itemIds.Contains)
			.Distinct(StringComparer.Ordinal)
			.ToArray();
		var activeItemId = !string.IsNullOrWhiteSpace(ActiveItemId)
			&& itemIds.Contains(ActiveItemId)
				? ActiveItemId
				: null;

		return this with
		{
			ActiveItemId = activeItemId,
			PausedItemIds = pausedItemIds
		};
	}

	/// <summary>Returns whether the item was explicitly paused by the user.</summary>
	public bool IsPaused(string itemId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
		return PausedItemIds.Contains(itemId, StringComparer.Ordinal);
	}
}

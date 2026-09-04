using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns atomic mutations of the project-independent ROOT document while its view model owns the
/// observable projection.
/// </summary>
internal sealed class RootTabsPersistenceCoordinator(IRootTabsStore store)
{
	private readonly IRootTabsStore _store =
		store ?? throw new ArgumentNullException(nameof(store));

	public Task<RootTabsRecord> AddSessionAsync(
		SessionRecord session,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record => record with
			{
				ActiveItemId = session.Id,
				Sessions = record.Sessions.Concat([session]).ToArray()
			},
			cancellationToken);

	public Task<RootTabsRecord> AddWebPageAsync(
		WebPageRecord webPage,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record => record with
			{
				ActiveItemId = webPage.Id,
				WebPages = record.WebPages.Concat([webPage]).ToArray()
			},
			cancellationToken);

	public Task<RootTabsRecord> MoveSessionAsync(
		string sourceId,
		string targetId,
		bool insertAfter,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record => record with
			{
				Sessions = SavedItemOrder.Move(
					record.Sessions,
					session => session.Id,
					sourceId,
					targetId,
					insertAfter)
			},
			cancellationToken);

	public Task<RootTabsRecord> MoveWebPageAsync(
		string sourceId,
		string targetId,
		bool insertAfter,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record => record with
			{
				WebPages = SavedItemOrder.Move(
					record.WebPages,
					webPage => webPage.Id,
					sourceId,
					targetId,
					insertAfter)
			},
			cancellationToken);

	public async Task<(RootTabsRecord Record, SessionRecord Session)?> UpdateSessionAsync(
		string sessionId,
		Func<SessionRecord, SessionRecord> mutate,
		CancellationToken cancellationToken)
	{
		SessionRecord? updated = null;
		var record = await _store.UpdateAsync(
			root => root with
			{
				Sessions = root.Sessions.Select(session =>
				{
					if (!string.Equals(session.Id, sessionId, StringComparison.Ordinal))
					{
						return session;
					}

					updated = mutate(session);
					return updated;
				}).ToArray()
			},
			cancellationToken);
		return updated is null ? null : (record, updated);
	}

	public async Task<(RootTabsRecord Record, WebPageRecord WebPage)?> UpdateWebPageAsync(
		string webPageId,
		Func<WebPageRecord, WebPageRecord> mutate,
		CancellationToken cancellationToken)
	{
		WebPageRecord? updated = null;
		var record = await _store.UpdateAsync(
			root => root with
			{
				WebPages = root.WebPages.Select(webPage =>
				{
					if (!string.Equals(webPage.Id, webPageId, StringComparison.Ordinal))
					{
						return webPage;
					}

					updated = mutate(webPage);
					return updated;
				}).ToArray()
			},
			cancellationToken);
		return updated is null ? null : (record, updated);
	}

	public Task<RootTabsRecord> SetActiveItemAsync(
		string itemId,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record => OwnsItem(record, itemId)
				? record with { ActiveItemId = itemId }
				: record,
			cancellationToken);

	public Task<RootTabsRecord> SetPausedAsync(
		string itemId,
		bool paused,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record =>
			{
				if (!OwnsItem(record, itemId))
				{
					return record;
				}

				var pausedIds = paused
					? record.PausedItemIds.Concat([itemId]).Distinct(StringComparer.Ordinal).ToArray()
					: record.PausedItemIds
						.Where(id => !string.Equals(id, itemId, StringComparison.Ordinal))
						.ToArray();
				return record with { PausedItemIds = pausedIds };
			},
			cancellationToken);

	public Task<RootTabsRecord> RemoveItemAsync(
		string itemId,
		string? replacementActiveItemId,
		CancellationToken cancellationToken) =>
		_store.UpdateAsync(
			record =>
			{
				var sessions = record.Sessions
					.Where(session => !string.Equals(session.Id, itemId, StringComparison.Ordinal))
					.ToArray();
				var webPages = record.WebPages
					.Where(webPage => !string.Equals(webPage.Id, itemId, StringComparison.Ordinal))
					.ToArray();
				if (sessions.Length == record.Sessions.Count
					&& webPages.Length == record.WebPages.Count)
				{
					return record;
				}

				var remaining = record with
				{
					Sessions = sessions,
					WebPages = webPages,
					PausedItemIds = record.PausedItemIds
						.Where(id => !string.Equals(id, itemId, StringComparison.Ordinal))
						.ToArray()
				};
				var activeItemId = string.Equals(record.ActiveItemId, itemId, StringComparison.Ordinal)
					&& !string.IsNullOrWhiteSpace(replacementActiveItemId)
					&& OwnsItem(remaining, replacementActiveItemId)
						? replacementActiveItemId
						: string.Equals(record.ActiveItemId, itemId, StringComparison.Ordinal)
							? null
							: record.ActiveItemId;
				return remaining with { ActiveItemId = activeItemId };
			},
			cancellationToken);

	private static bool OwnsItem(RootTabsRecord record, string itemId) =>
		record.Sessions.Any(session => string.Equals(session.Id, itemId, StringComparison.Ordinal))
		|| record.WebPages.Any(webPage => string.Equals(webPage.Id, itemId, StringComparison.Ordinal));
}

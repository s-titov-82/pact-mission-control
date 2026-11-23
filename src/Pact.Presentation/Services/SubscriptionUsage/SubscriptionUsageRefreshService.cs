using System.Collections.ObjectModel;
using Pact.Core.Agents;

namespace Pact.Presentation.Services;
/// <summary>
/// Refreshes the usage rows shown in the subscription panel.
/// </summary>
public sealed class SubscriptionUsageRefreshService
{
	private readonly ISubscriptionUsageReader _reader;

	/// <summary>Creates a refresh service over <paramref name="reader"/>.</summary>
	public SubscriptionUsageRefreshService(ISubscriptionUsageReader reader)
	{
		_reader = reader;
	}

	/// <summary>
	/// Refreshes Codex and Claude usage, retaining the existing row on a display-name change.
	/// </summary>
	/// <remarks>
	/// Creates rows for new profiles, clears prior account data when the profile configuration
	/// changes, and removes rows for profiles no longer present.
	/// </remarks>
	public async Task RefreshAsync(
		IEnumerable<AgentProfileRecord> profiles,
		ObservableCollection<SubscriptionUsageRow> rows,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(rows);
		var usageProfiles = profiles
			.Where(profile => profile.Kind is AgentKind.Codex or AgentKind.Claude)
			.ToArray();

		foreach (var profile in usageProfiles)
		{
			var row = rows.FirstOrDefault(item => item.ProfileId == profile.Id);
			if (row is null)
			{
				row = new SubscriptionUsageRow(profile);
				rows.Add(row);
			}
			else if (row.Profile with { DisplayName = profile.DisplayName } != profile)
			{
				var index = rows.IndexOf(row);
				row = new SubscriptionUsageRow(profile);
				rows[index] = row;
			}
			else
			{
				row.UpdateDisplayName(profile.DisplayName);
			}

			// Readers walk agent data on disk and start helper processes synchronously before
			// their first await; the rows stay bound to the caller's context.
			var snapshot = await Task.Run(
				() => _reader.ReadAsync(profile, cancellationToken),
				cancellationToken);
			row.Apply(snapshot);
		}

		var activeProfileIds = usageProfiles
			.Select(profile => profile.Id)
			.ToHashSet(StringComparer.Ordinal);

		for (var index = rows.Count - 1; index >= 0; index--)
		{
			if (!activeProfileIds.Contains(rows[index].ProfileId))
			{
				rows.RemoveAt(index);
			}
		}
	}
}

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
	/// Reads usage for every profile that reports it and applies the results to the matching
	/// rows in place, so the bound collection is not rebuilt.
	/// </summary>
	/// <remarks>
	/// Profiles with no matching row are skipped, and each profile is read independently so one
	/// failing agent does not prevent the others from updating.
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

		var rowsByProfileId = rows.ToDictionary(
			row => row.ProfileId,
			StringComparer.Ordinal);

		foreach (var profile in usageProfiles)
		{
			if (!rowsByProfileId.TryGetValue(profile.Id, out var row))
			{
				row = new SubscriptionUsageRow(profile);
				rows.Add(row);
			}

			var snapshot = await _reader.ReadAsync(profile, cancellationToken);
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
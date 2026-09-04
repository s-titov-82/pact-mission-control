using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.AgentControl;

namespace Pact.Infrastructure.Settings;

/// <summary>Reads reviewer-only launch profiles without taking ownership of saving the file.</summary>
public static class ReviewProfileReader
{
	/// <summary>
	/// Reads valid profile entries in file order. A missing or malformed file yields an empty
	/// snapshot, and malformed entries are skipped so one hand edit cannot hide every profile.
	/// </summary>
	public static async Task<IReadOnlyList<ReviewProfile>> ReadAsync(
		string path,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (!File.Exists(path))
		{
			return [];
		}

		JsonArray entries;
		try
		{
			var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
			entries = JsonNode.Parse(json) as JsonArray ?? [];
		}
		catch (JsonException)
		{
			return [];
		}

		List<ReviewProfile> profiles = [];
		foreach (var entry in entries)
		{
			try
			{
				var profile = entry?.Deserialize<ReviewProfile>(SettingsFileStore.JsonOptions);
				if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
				{
					profiles.Add(profile);
				}
			}
			catch (JsonException)
			{
				// Skip only the invalid entry; the remaining hand-edited profiles stay usable.
			}
		}

		return profiles;
	}
}

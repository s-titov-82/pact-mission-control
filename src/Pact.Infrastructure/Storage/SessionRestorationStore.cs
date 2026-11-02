using System.Text.Json;
using Pact.Core.Updates;

namespace Pact.Infrastructure.Storage;

/// <summary>Atomically retains content-free startup metadata under Settings.</summary>
public sealed class SessionRestorationStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly string _path;

	/// <summary>Creates a store for one data root's startup checkpoint.</summary>
	public SessionRestorationStore(string path)
	{
		_path = path;
	}

	/// <summary>Loads a supported checkpoint; missing or malformed data yields no restoration.</summary>
	public async Task<SessionRestorationState?> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_path))
		{
			return null;
		}
		try
		{
			await using var stream = File.OpenRead(_path);
			if (stream.Length > 1024 * 1024)
			{
				return null;
			}
			var state = await JsonSerializer.DeserializeAsync<SessionRestorationState>(stream, JsonOptions, cancellationToken);
			return state is { SchemaVersion: 1 }
				&& ValidIds(state.LiveTerminalIds) && ValidIds(state.LoadedWebPageIds) && ValidIds(state.UnreadTerminalIds)
				? state : null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	/// <summary>Writes a complete immutable projection; the caller serializes updates.</summary>
	public Task SaveAsync(SessionRestorationState state, CancellationToken cancellationToken) =>
		AtomicFileWriter.WriteTextAsync(_path, JsonSerializer.Serialize(state, JsonOptions), cancellationToken);

	private static bool ValidIds(IReadOnlyList<string>? ids) => ids is { Count: <= 10000 }
		&& ids.All(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 256);
}

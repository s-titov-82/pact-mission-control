using System.Text.Json;
using Pact.Core.Orchestrator;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>Persists the dedicated orchestrator slot outside the raw settings editor.</summary>
public sealed class OrchestratorStore
{
	private readonly string? _stagingDirectory;

	/// <summary>Creates a store for one orchestrator JSON document.</summary>
	public OrchestratorStore(string path, string? stagingDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Path = path;
		_stagingDirectory = stagingDirectory;
	}

	/// <summary>Gets the orchestrator JSON path.</summary>
	public string Path { get; }

	/// <summary>
	/// Loads the orchestrator slot, returning a disabled default when the document is missing
	/// or unreadable.
	/// </summary>
	public async Task<OrchestratorRecord> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(Path))
		{
			return OrchestratorRecord.CreateDefault();
		}

		try
		{
			await using var stream = File.OpenRead(Path);
			return await JsonSerializer.DeserializeAsync<OrchestratorRecord>(
				stream,
				SettingsFileStore.JsonOptions,
				cancellationToken).ConfigureAwait(false)
				?? OrchestratorRecord.CreateDefault();
		}
		catch (JsonException)
		{
			return OrchestratorRecord.CreateDefault();
		}
		catch (IOException)
		{
			return OrchestratorRecord.CreateDefault();
		}
	}

	/// <summary>Saves the complete orchestrator slot atomically.</summary>
	public Task SaveAsync(
		OrchestratorRecord record,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);
		var json = JsonSerializer.Serialize(record, SettingsFileStore.JsonOptions);
		return AtomicFileWriter.WriteTextAsync(
			Path,
			json,
			_stagingDirectory,
			cancellationToken);
	}
}

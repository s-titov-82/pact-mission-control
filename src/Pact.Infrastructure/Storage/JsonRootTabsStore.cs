using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Stores project-independent terminal and browser tabs in
/// <c>Settings/root-tabs.json</c>, preserving unknown JSON nodes on known-item updates.
/// </summary>
public sealed class JsonRootTabsStore : IRootTabsStore
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};
	private readonly AppPaths _paths;

	/// <summary>Creates a store over a data-root directory.</summary>
	public JsonRootTabsStore(string rootDirectory)
		: this(new AppPaths(rootDirectory))
	{
	}

	/// <summary>Creates a store over an existing path layout.</summary>
	public JsonRootTabsStore(AppPaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	/// <inheritdoc />
	public Task<RootTabsRecord> LoadAsync(CancellationToken cancellationToken) =>
		LoadUnlockedAsync(cancellationToken);

	/// <inheritdoc />
	public async Task SaveAsync(RootTabsRecord document, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(document);
		await WithLockAsync(
			async () =>
			{
				await SaveUnlockedAsync(document.Normalize(), cancellationToken).ConfigureAwait(false);
				return true;
			},
			cancellationToken).ConfigureAwait(false);
	}

	/// <inheritdoc />
	public Task<RootTabsRecord> UpdateAsync(
		Func<RootTabsRecord, RootTabsRecord> update,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(update);
		return WithLockAsync(
			async () =>
			{
				var document = await LoadForUpdateUnlockedAsync(cancellationToken).ConfigureAwait(false);
				var updated = update(document).Normalize();
				await SaveUnlockedAsync(updated, cancellationToken).ConfigureAwait(false);
				return updated;
			},
			cancellationToken);
	}

	private async Task<RootTabsRecord> LoadUnlockedAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.RootTabsPath))
		{
			return RootTabsRecord.CreateDefault();
		}

		try
		{
			var json = await File.ReadAllTextAsync(_paths.RootTabsPath, cancellationToken)
				.ConfigureAwait(false);
			return Deserialize(json);
		}
		catch (JsonException)
		{
			return RootTabsRecord.CreateDefault();
		}
		catch (NotSupportedException)
		{
			return RootTabsRecord.CreateDefault();
		}
		catch (InvalidDataException)
		{
			return RootTabsRecord.CreateDefault();
		}
	}

	private async Task<RootTabsRecord> LoadForUpdateUnlockedAsync(
		CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.RootTabsPath))
		{
			return RootTabsRecord.CreateDefault();
		}

		var json = await File.ReadAllTextAsync(_paths.RootTabsPath, cancellationToken)
			.ConfigureAwait(false);
		try
		{
			return Deserialize(json);
		}
		catch (Exception exception) when (
			exception is JsonException or NotSupportedException or InvalidDataException)
		{
			throw new InvalidDataException(
				"root-tabs.json is malformed and cannot be updated without losing data.",
				exception);
		}
	}

	private static RootTabsRecord Deserialize(string json)
	{
		var document = JsonSerializer.Deserialize<RootTabsRecord>(json, JsonOptions)
			?? RootTabsRecord.CreateDefault();
		var sessions = document.Sessions
			.Select(session => session.Status is SessionStatus.Starting or SessionStatus.Running
				? session with { Status = SessionStatus.Stopped }
				: session)
			.ToArray();
		return (document with { Sessions = sessions }).Normalize();
	}

	private async Task SaveUnlockedAsync(
		RootTabsRecord document,
		CancellationToken cancellationToken)
	{
		var updated = JsonSerializer.SerializeToNode(document, JsonOptions)
			as JsonObject
			?? throw new JsonException("Root tabs document did not serialize as an object.");
		var output = await MergeExistingUnknownNodesAsync(updated, cancellationToken)
			.ConfigureAwait(false);
		await AtomicFileWriter.WriteTextAsync(
			_paths.RootTabsPath,
			output.ToJsonString(JsonOptions),
			_paths.AtomicTempDirectory,
			cancellationToken).ConfigureAwait(false);
	}

	private async Task<JsonObject> MergeExistingUnknownNodesAsync(
		JsonObject updated,
		CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.RootTabsPath))
		{
			return updated;
		}

		JsonObject? existing;
		try
		{
			var json = await File.ReadAllTextAsync(_paths.RootTabsPath, cancellationToken)
				.ConfigureAwait(false);
			existing = JsonNode.Parse(json) as JsonObject;
		}
		catch (JsonException)
		{
			return updated;
		}

		if (existing is null)
		{
			return updated;
		}

		var merged = (JsonObject)existing.DeepClone();
		foreach (var property in updated)
		{
			if (property.Key is "sessions" or "webPages")
			{
				continue;
			}

			merged[property.Key] = property.Value?.DeepClone();
		}

		merged["sessions"] = MergeItemArray<SessionRecord>(
			existing["sessions"] as JsonArray,
			updated["sessions"] as JsonArray);
		merged["webPages"] = MergeItemArray<WebPageRecord>(
			existing["webPages"] as JsonArray,
			updated["webPages"] as JsonArray);
		return merged;
	}

	private static JsonArray MergeItemArray<T>(JsonArray? existing, JsonArray? updated)
		where T : class
	{
		var result = new JsonArray();
		var existingById = (existing ?? [])
			.OfType<JsonObject>()
			.Where(item => TryGetId(item, out _))
			.GroupBy(
				item =>
				{
					TryGetId(item, out var id);
					return id;
				},
				StringComparer.Ordinal)
			.ToDictionary(
				group => group.Key,
				group => group.First(),
				StringComparer.Ordinal);
		var updatedIds = new HashSet<string>(StringComparer.Ordinal);

		foreach (var node in updated ?? [])
		{
			if (node is not JsonObject updatedItem || !TryGetId(updatedItem, out var id))
			{
				result.Add(node?.DeepClone());
				continue;
			}

			updatedIds.Add(id);
			if (!existingById.TryGetValue(id, out var existingItem))
			{
				result.Add(updatedItem.DeepClone());
				continue;
			}

			var mergedItem = (JsonObject)existingItem.DeepClone();
			foreach (var property in updatedItem)
			{
				mergedItem[property.Key] = property.Value?.DeepClone();
			}

			result.Add(mergedItem);
		}

		foreach (var node in existing ?? [])
		{
			if (node is JsonObject existingItem
				&& TryGetId(existingItem, out var id)
				&& updatedIds.Contains(id))
			{
				continue;
			}

			if (!CanDeserialize<T>(node))
			{
				result.Add(node?.DeepClone());
			}
		}

		return result;
	}

	private static bool TryGetId(JsonObject item, out string id)
	{
		id = item["id"] is JsonValue value
			&& value.TryGetValue<string>(out var stringId)
				? stringId
				: string.Empty;
		return !string.IsNullOrWhiteSpace(id);
	}

	private static bool CanDeserialize<T>(JsonNode? node)
		where T : class
	{
		if (node is null)
		{
			return false;
		}

		try
		{
			return node.Deserialize<T>(JsonOptions) is not null;
		}
		catch (Exception exception) when (
			exception is JsonException or NotSupportedException or InvalidOperationException)
		{
			return false;
		}
	}

	private async Task<T> WithLockAsync<T>(
		Func<Task<T>> action,
		CancellationToken cancellationToken)
	{
		var rootTabsPath = Path.GetFullPath(_paths.RootTabsPath);
		var processLock = ProcessLocks.GetOrAdd(
			rootTabsPath,
			static _ => new SemaphoreSlim(1, 1));
		await processLock.WaitAsync(cancellationToken).ConfigureAwait(false);
		RootTabsSemaphoreHandle? semaphoreHandle = null;
		try
		{
			semaphoreHandle = await RootTabsSemaphoreHandle.WaitAsync(
				rootTabsPath,
				cancellationToken).ConfigureAwait(false);
			return await action().ConfigureAwait(false);
		}
		finally
		{
			try
			{
				semaphoreHandle?.Dispose();
			}
			finally
			{
				processLock.Release();
			}
		}
	}

	private sealed class RootTabsSemaphoreHandle : IDisposable
	{
		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
		private readonly Semaphore? _semaphore;
		private readonly bool _ownsSemaphore;

		private RootTabsSemaphoreHandle(Semaphore? semaphore, bool ownsSemaphore)
		{
			_semaphore = semaphore;
			_ownsSemaphore = ownsSemaphore;
		}

		public static async Task<RootTabsSemaphoreHandle> WaitAsync(
			string path,
			CancellationToken cancellationToken)
		{
			Semaphore? semaphore = null;
			try
			{
				semaphore = new Semaphore(1, 1, CreateSemaphoreName(path));
				while (!semaphore.WaitOne(PollInterval))
				{
					cancellationToken.ThrowIfCancellationRequested();
					await Task.Yield();
				}

				return new RootTabsSemaphoreHandle(semaphore, ownsSemaphore: true);
			}
			catch (PlatformNotSupportedException)
			{
				semaphore?.Dispose();
				return new RootTabsSemaphoreHandle(null, ownsSemaphore: false);
			}
			catch (UnauthorizedAccessException)
			{
				semaphore?.Dispose();
				return new RootTabsSemaphoreHandle(null, ownsSemaphore: false);
			}
			catch
			{
				semaphore?.Dispose();
				throw;
			}
		}

		public void Dispose()
		{
			if (_ownsSemaphore)
			{
				_semaphore?.Release();
			}

			_semaphore?.Dispose();
		}

		private static string CreateSemaphoreName(string path)
		{
			var bytes = Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant());
			return $"Pact.RootTabs.{Convert.ToHexString(SHA256.HashData(bytes))}";
		}
	}
}

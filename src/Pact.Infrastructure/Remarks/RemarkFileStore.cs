using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Remarks;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Remarks;

/// <summary>A project's remark threads as stored.</summary>
/// <param name="NextNumber">The number of the next id; ids are never reused.</param>
/// <param name="Threads">Every thread.</param>
/// <param name="SetAsideFile">
/// Where an unreadable file was moved on load, so its remarks are kept for the user; otherwise null.
/// </param>
public sealed record RemarkFile(int NextNumber, IReadOnlyList<RemarkThread> Threads, string? SetAsideFile = null);

/// <summary>
/// Stores a project's remark threads in <c>.pact/remarks/remarks.json</c> and the file copies their
/// anchors were written on in <c>.pact/remarks/snapshots</c>, named by the hash of their text.
/// </summary>
public sealed class RemarkFileStore
{
	private const int Version = 1;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};

	private readonly string _projectRoot;
	private readonly string _folder;

	/// <summary>Creates the store of one project.</summary>
	public RemarkFileStore(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		_projectRoot = projectRoot;
		_folder = Path.Combine(projectRoot, ".pact", "remarks");
	}

	private string FilePath => Path.Combine(_folder, "remarks.json");

	private string SnapshotFolder => Path.Combine(_folder, "snapshots");

	/// <summary>
	/// Reads the threads. A missing file reads as none; an unreadable one is moved aside as
	/// <c>remarks.json.broken-&lt;stamp&gt;</c> and reported, never overwritten.
	/// </summary>
	public async Task<RemarkFile> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(FilePath))
		{
			return new RemarkFile(1, []);
		}

		var json = await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false);
		try
		{
			var document = JsonSerializer.Deserialize<RemarkDocument>(json, JsonOptions);
			if (document?.Threads is { } threads)
			{
				return new RemarkFile(Math.Max(1, document.NextNumber), threads);
			}
		}
		catch (JsonException)
		{
			// Set aside below.
		}

		var stamp = DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
		var aside = Path.Combine(_folder, $"remarks.json.broken-{stamp}");
		File.Move(FilePath, aside, overwrite: true);
		return new RemarkFile(1, [], aside);
	}

	/// <summary>Writes the threads atomically and keeps <c>.pact</c> out of Git.</summary>
	public async Task SaveAsync(RemarkFile file, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(file);
		PactFolder.EnsureGitIgnored(_projectRoot);
		var json = JsonSerializer.Serialize(new RemarkDocument(Version, file.NextNumber, file.Threads), JsonOptions);
		await AtomicFileWriter.WriteTextAsync(FilePath, json, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Stores a copy of a file's text once and returns its id, the lower-case SHA-256 of its UTF-8 bytes.</summary>
	public async Task<string> SaveSnapshotAsync(string text, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
		var path = Path.Combine(SnapshotFolder, $"{id}.txt");
		if (!File.Exists(path))
		{
			PactFolder.EnsureGitIgnored(_projectRoot);
			await AtomicFileWriter.WriteTextAsync(path, text, cancellationToken).ConfigureAwait(false);
		}

		return id;
	}

	/// <summary>Reads a stored copy as lines, or null when it is missing.</summary>
	public async Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string id, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(id);
		if (id.Length != 64 || !id.All(char.IsAsciiHexDigitLower))
		{
			return null;
		}

		var path = Path.Combine(SnapshotFolder, $"{id}.txt");
		return File.Exists(path)
			? RemarkLocator.SplitLines(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false))
			: null;
	}

	private sealed record RemarkDocument(int Version, int NextNumber, IReadOnlyList<RemarkThread>? Threads);
}

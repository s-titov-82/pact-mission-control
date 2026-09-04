using System.Text.Json;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>
/// Most-recently-used project directories, offered when adding a project. Capped at twenty
/// entries, newest first.
/// </summary>
public sealed class RecentDirectoryStore
{
	private const int MaxRecentDirectories = 20;
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
	private readonly string _path;
	private readonly string? _stagingDirectory;

	/// <summary>
	/// Creates a store over <paramref name="path"/>.
	/// </summary>
	/// <param name="path">JSON file holding the list.</param>
	/// <param name="stagingDirectory">
	/// Directory for atomic-write staging, or <see langword="null"/> to stage beside the file.
	/// </param>
	public RecentDirectoryStore(string path, string? stagingDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		_path = path;
		_stagingDirectory = stagingDirectory;
	}

	/// <summary>
	/// Reads the recent directories, newest first.
	/// </summary>
	/// <returns>
	/// The list, empty when the file does not exist yet. Blank and duplicate entries are dropped
	/// on read, so a hand-edited file cannot produce a broken menu.
	/// </returns>
	public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_path))
		{
			return [];
		}

		await using var stream = File.OpenRead(_path);
		var directories = await JsonSerializer.DeserializeAsync<string[]>(stream, cancellationToken: cancellationToken);

		return directories?
			.Where(directory => !string.IsNullOrWhiteSpace(directory))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Take(MaxRecentDirectories)
			.ToArray()
			?? [];
	}

	/// <summary>
	/// Moves <paramref name="directory"/> to the front of the list, dropping any earlier
	/// occurrence and trimming the tail past the cap. The write is atomic.
	/// </summary>
	public async Task AddAsync(string directory, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);

		var existingDirectories = await LoadAsync(cancellationToken);
		var directories = existingDirectories
			.Where(existingDirectory => !string.Equals(existingDirectory, directory, StringComparison.OrdinalIgnoreCase))
			.Prepend(directory)
			.Take(MaxRecentDirectories)
			.ToArray();

		var json = JsonSerializer.Serialize(directories, JsonOptions);
		await AtomicFileWriter.WriteTextAsync(_path, json, _stagingDirectory, cancellationToken);
	}
}

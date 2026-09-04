using System.Text.Json;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Editable recent-directories.json: no tabs, just a list of paths as free text.</summary>
public sealed class RecentFoldersSectionViewModel : SettingsSectionViewModelBase
{
	private readonly SettingsFileStore _store;

	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public RecentFoldersSectionViewModel(SettingsFileStore store)
		: base(
			SettingsSection.RecentFolders,
			"Recent directories",
			"Recent startup directories for the new-session dialog.",
			"recent-directories.json",
			ResolvePath(store))
	{
		ArgumentNullException.ThrowIfNull(store);
		_store = store;
	}

	/// <summary>One directory path per line.</summary>
	public string FoldersText
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				MarkDirty();
			}
		}
	} = string.Empty;

	/// <summary>
	/// Appends a picked directory as a new line (backs the "Add directory" button). Deduplication
	/// and normalization still happen in <see cref="SaveAsync"/>, same as manual edits.
	/// </summary>
	public void AddDirectory(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		FoldersText = string.IsNullOrWhiteSpace(FoldersText)
			? path
			: $"{FoldersText.TrimEnd('\r', '\n')}\n{path}";
	}

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		StatusText = null;
		// No ConfigureAwait(false): this method mutates the UI-bound FoldersText/StatusText
		// afterwards, so the continuation must stay on the captured SynchronizationContext.
		var json = await _store.ReadAsync(FileName, cancellationToken);

		try
		{
			var folders = JsonSerializer.Deserialize<string[]>(json, SettingsFileStore.JsonOptions) ?? [];
			FoldersText = string.Join('\n', folders);
		}
		catch (JsonException ex)
		{
			StatusText = $"Failed to load {FileName}: {ex.Message}";
			FoldersText = string.Empty;
		}

		ClearDirty();
	}

	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		var normalized = FoldersText
			.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		var json = JsonSerializer.Serialize(normalized, SettingsFileStore.JsonOptions);
		// No ConfigureAwait(false): ClearDirty()/StatusText below are UI-bound.
		await _store.SaveAsync(FileName, json, cancellationToken);
		ClearDirty();
		StatusText = $"Saved {Label} ({normalized.Length} items).";
		return true;
	}

	private static string ResolvePath(SettingsFileStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		var descriptor = store.Files.First(
			file => string.Equals(file.FileName, "recent-directories.json", StringComparison.OrdinalIgnoreCase));
		return descriptor.Path;
	}
}

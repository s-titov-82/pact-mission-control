using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// What the directory picker produced.
/// </summary>
/// <param name="Directory">Chosen directory, verified to exist.</param>
public sealed record DirectorySelectionResult(string Directory);

/// <summary>
/// Backs the directory dialog used when adding a project: free-text entry plus the recent list.
/// </summary>
public sealed class DirectorySelectionViewModel : SettingsObservableObject
{
	private string _directoryText;

	/// <summary>Creates the model over the recent-directory list.</summary>
	public DirectorySelectionViewModel(
		IEnumerable<string> recentDirectories,
		string initialDirectory)
	{
		ArgumentNullException.ThrowIfNull(recentDirectories);

		RecentDirectories = recentDirectories
			.Where(directory => !string.IsNullOrWhiteSpace(directory))
			.ToArray();
		_directoryText = initialDirectory ?? string.Empty;
	}

	/// <summary>Recently used directories, newest first.</summary>
	public IReadOnlyList<string> RecentDirectories { get; }

	/// <summary>Directory path as typed; validated against the filesystem.</summary>
	public string DirectoryText
	{
		get => _directoryText;
		set
		{
			if (!SetField(ref _directoryText, value ?? string.Empty))
			{
				return;
			}

			OnPropertyChanged(nameof(CanAccept));
			OnPropertyChanged(nameof(ValidationMessage));
		}
	}

	/// <summary>Recent entry chosen from the list, which fills <see cref="DirectoryText"/>.</summary>
	public string? SelectedRecentDirectory
	{
		get;
		set
		{
			if (!SetField(ref field, value))
			{
				return;
			}

			if (value is not null)
			{
				DirectoryText = value;
			}
		}
	}

	/// <summary>Whether the typed path names an existing directory.</summary>
	public bool CanAccept => Directory.Exists(DirectoryText.Trim());

	/// <summary>Why the path is unusable, empty while it is valid or still blank.</summary>
	public string ValidationMessage => CanAccept || string.IsNullOrWhiteSpace(DirectoryText)
		? string.Empty
		: "Directory does not exist.";

	/// <summary>Builds the result, or <see langword="null"/> when the path does not exist.</summary>
	public DirectorySelectionResult? CreateResult()
	{
		var directory = DirectoryText.Trim();
		return Directory.Exists(directory)
			? new DirectorySelectionResult(Path.GetFullPath(directory))
			: null;
	}
}
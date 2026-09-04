namespace Pact.Core.Platform;

/// <summary>
/// Presents the platform folder-selection dialog used when adding a project root.
/// </summary>
public interface IFolderPicker
{
	/// <summary>
	/// Prompts for a directory.
	/// </summary>
	/// <param name="initialDirectory">
	/// Directory to open at, or <see langword="null"/> to let the platform choose. A path
	/// that no longer exists is ignored rather than treated as an error.
	/// </param>
	/// <param name="title">Dialog caption.</param>
	/// <returns>The selected path, or <see langword="null"/> when the user cancels.</returns>
	Task<string?> PickFolderAsync(string? initialDirectory, string title);
}
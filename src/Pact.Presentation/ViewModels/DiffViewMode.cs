namespace Pact.Presentation.ViewModels;

/// <summary>How the Changes tab shows the selected file.</summary>
public enum DiffViewMode
{
	/// <summary>The hunks with a few lines of context.</summary>
	Diff,

	/// <summary>The new version in full, removed runs folded where they were.</summary>
	WholeFile,

	/// <summary>A Markdown file's new version rendered, its changed blocks marked.</summary>
	Rendered
}

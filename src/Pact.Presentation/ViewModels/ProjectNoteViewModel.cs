using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Projects;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// The project tree entry for a project's Docs &amp; Notes tab. The note text itself lives in
/// <see cref="Services.ProjectNoteDocument"/>; this only represents the tab.
/// </summary>
public sealed class ProjectNoteViewModel : INotifyPropertyChanged
{

	/// <summary>
	/// Creates a view model over a notes tab.
	/// </summary>
	/// <param name="record">Persisted tab state.</param>
	/// <param name="projectRootPath">Owning project's root, used to locate the notes file.</param>
	public ProjectNoteViewModel(NotesTabRecord record, string projectRootPath)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		Record = record;
		ProjectRootPath = projectRootPath;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Persisted tab state.</summary>
	public NotesTabRecord Record { get; private set; }

	/// <summary>Owning project's root directory.</summary>
	public string ProjectRootPath { get; }

	/// <summary>Fixed tab label; the notes tab is not user-renamable.</summary>
	public static string Title => "Docs & Notes";

	/// <summary>Discriminator letting the tree template this row as a docs page.</summary>
	public static string PageKind => "docs";

	/// <summary>Whether this tab is the selected item.</summary>
	public bool IsCurrentNote { get; private set; }

	/// <summary>Sets the selected state, notifying only on a real change.</summary>
	public void SetCurrentNote(bool value) { if (IsCurrentNote == value) { return; } IsCurrentNote = value; OnPropertyChanged(nameof(IsCurrentNote)); }

	/// <summary>Replaces the persisted state after a save.</summary>
	public void UpdateRecord(NotesTabRecord record) { Record = record; OnPropertyChanged(nameof(Record)); }

	private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
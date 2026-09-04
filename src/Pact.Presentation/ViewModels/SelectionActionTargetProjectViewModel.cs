using System.Collections.ObjectModel;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// One project group in the selection-action target list, holding the sessions that can receive
/// the selection plus the project's notes target.
/// </summary>
public sealed class SelectionActionTargetProjectViewModel
{
	/// <summary>Creates a group with no notes target.</summary>
	public SelectionActionTargetProjectViewModel(WorkspaceViewModel workspace, IEnumerable<SessionViewModel> sessions)
		: this(workspace, sessions, null)
	{
	}

	/// <summary>
	/// Creates a group.
	/// </summary>
	/// <param name="workspace">Project this group represents.</param>
	/// <param name="sessions">Sessions eligible to receive the selection.</param>
	/// <param name="notesTarget">Notes target, or <see langword="null"/> when notes are unavailable.</param>
	/// <param name="isExpanded">Whether the group starts expanded.</param>
	public SelectionActionTargetProjectViewModel(WorkspaceViewModel workspace, IEnumerable<SessionViewModel> sessions, ProjectNotesTargetViewModel? notesTarget, bool isExpanded = false)
		: this(
			workspace?.Id ?? throw new ArgumentNullException(nameof(workspace)),
			workspace.Name,
			workspace.RootPath,
			sessions,
			notesTarget,
			isExpanded)
	{
	}

	/// <summary>Creates the project-independent ROOT target group.</summary>
	/// <param name="sessions">ROOT sessions eligible to receive the selection.</param>
	/// <param name="isExpanded">Whether the group starts expanded.</param>
	public static SelectionActionTargetProjectViewModel CreateRoot(
		IEnumerable<SessionViewModel> sessions,
		bool isExpanded = false) =>
		new("root", "ROOT", "Project-independent", sessions, null, isExpanded);

	private SelectionActionTargetProjectViewModel(
		string id,
		string name,
		string rootPath,
		IEnumerable<SessionViewModel> sessions,
		ProjectNotesTargetViewModel? notesTarget,
		bool isExpanded)
	{
		Id = id;
		Name = name;
		RootPath = rootPath;
		Sessions = new ObservableCollection<SessionViewModel>(sessions);
		NotesTarget = notesTarget;
		IsExpanded = isExpanded;
	}

	/// <summary>Project id.</summary>
	public string Id { get; }

	/// <summary>Project display name.</summary>
	public string Name { get; }

	/// <summary>Project root directory.</summary>
	public string RootPath { get; }

	/// <summary>
	/// Sessions offered as targets. Excludes the source session and any session locked by a
	/// running scenario.
	/// </summary>
	public ObservableCollection<SessionViewModel> Sessions { get; }

	/// <summary>Notes target, or <see langword="null"/> when this project has none.</summary>
	public ProjectNotesTargetViewModel? NotesTarget { get; }

	/// <summary>Whether a notes target is available.</summary>
	public bool HasNotesTarget => NotesTarget is not null;

	/// <summary>Whether the group is shown expanded, used for the project the selection came from.</summary>
	public bool IsExpanded { get; }
}

/// <summary>
/// The "send to notes" target for one project.
/// </summary>
/// <param name="ProjectId">Project whose notes receive the text.</param>
/// <param name="ProjectName">Project display name.</param>
public sealed record ProjectNotesTargetViewModel(string ProjectId, string ProjectName)
{
	/// <summary>Label shown for this target in the list.</summary>
	public static string Title => "Notes";
}

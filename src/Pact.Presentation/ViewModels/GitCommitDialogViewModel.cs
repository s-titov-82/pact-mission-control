using System.Collections.ObjectModel;
using Pact.Core.Git;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// What the commit dialog produced.
/// </summary>
/// <param name="Message">Commit message.</param>
/// <param name="Files">Paths the user chose to include.</param>
public sealed record GitCommitDialogResult(
	string Message,
	IReadOnlyList<GitFileEntry> Files);

/// <summary>Backs the commit dialog: the message and per-file inclusion choices.</summary>
public sealed class GitCommitDialogViewModel : SettingsObservableObject
{
	private bool _updatingSelection;

	/// <summary>Creates the dialog model over the working tree's changed files, all selected.</summary>
	public GitCommitDialogViewModel(IReadOnlyList<GitFileEntry> files)
	{
		ArgumentNullException.ThrowIfNull(files);

		Files = new ObservableCollection<GitCommitFileChoiceViewModel>(
			files.Select(file => new GitCommitFileChoiceViewModel(
				file,
				OnFileSelectionChanged)));
	}

	/// <summary>Changed files with their inclusion choices.</summary>
	public ObservableCollection<GitCommitFileChoiceViewModel> Files { get; }

	/// <summary>Commit message; a blank message blocks accepting.</summary>
	public string Message
	{
		get;
		set
		{
			if (SetField(ref field, value ?? string.Empty))
			{
				OnPropertyChanged(nameof(CanAccept));
			}
		}
	} = string.Empty;

	/// <summary>Whether every file is selected, driving the select-all control.</summary>
	public bool AreAllFilesSelected => Files.Count > 0 && Files.All(file => file.IsSelected);

	/// <summary>Whether the dialog can commit: a message plus at least one selected file.</summary>
	public bool CanAccept => !string.IsNullOrWhiteSpace(Message)
		&& Files.Any(file => file.IsSelected);

	/// <summary>Selects or clears every file at once.</summary>
	public void SetAllSelected(bool selected)
	{
		_updatingSelection = true;
		try
		{
			foreach (var file in Files)
			{
				file.IsSelected = selected;
			}
		}
		finally
		{
			_updatingSelection = false;
		}

		NotifySelectionStateChanged();
	}

	/// <summary>
	/// Builds the result, or <see langword="null"/> when the dialog is not in an acceptable state.
	/// </summary>
	public GitCommitDialogResult? CreateResult() => CanAccept
		? new GitCommitDialogResult(
			Message.Trim(),
			Files.Where(file => file.IsSelected).Select(file => file.Entry).ToArray())
		: null;

	private void OnFileSelectionChanged()
	{
		if (!_updatingSelection)
		{
			NotifySelectionStateChanged();
		}
	}

	private void NotifySelectionStateChanged()
	{
		OnPropertyChanged(nameof(AreAllFilesSelected));
		OnPropertyChanged(nameof(CanAccept));
	}
}

/// <summary>One changed file and whether it is included in the commit.</summary>
public sealed class GitCommitFileChoiceViewModel : SettingsObservableObject
{
	private readonly Action _selectionChanged;

	internal GitCommitFileChoiceViewModel(
		GitFileEntry entry,
		Action selectionChanged)
	{
		Entry = entry;
		_selectionChanged = selectionChanged;
	}

	/// <summary>Underlying status entry.</summary>
	public GitFileEntry Entry { get; }

	/// <summary>Whether this file is included.</summary>
	public bool IsSelected
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				_selectionChanged();
			}
		}
	} = true;

	/// <summary>Single-character change indicator shown beside the path.</summary>
	public string Marker => Entry.Kind switch
	{
		GitChangeKind.Added => "+",
		GitChangeKind.Modified => "~",
		GitChangeKind.Deleted => "-",
		GitChangeKind.Untracked => "?",
		GitChangeKind.Conflicted => "!",
		_ => string.Empty
	};

	/// <summary>Path for display, showing both sides of a rename.</summary>
	public string DisplayPath => string.IsNullOrWhiteSpace(Entry.OriginalPath)
		? Entry.Path
		: $"{Entry.OriginalPath} -> {Entry.Path}";
}
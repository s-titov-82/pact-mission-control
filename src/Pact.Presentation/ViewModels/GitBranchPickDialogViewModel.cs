using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// What the branch picker produced.
/// </summary>
/// <param name="Branch">Chosen or newly named branch.</param>
/// <param name="Create">Whether the branch must be created.</param>
public sealed record GitBranchPickDialogResult(string Branch, bool Create);

/// <summary>Backs the branch picker: choose an existing branch or name a new one.</summary>
public sealed class GitBranchPickDialogViewModel : SettingsObservableObject
{

	/// <summary>Creates the picker over the known branches.</summary>
	public GitBranchPickDialogViewModel(
		IReadOnlyList<string> branches,
		bool allowCreate)
	{
		ArgumentNullException.ThrowIfNull(branches);

		Branches = branches.ToArray();
		AllowCreate = allowCreate;
	}

	/// <summary>Existing branches to choose from.</summary>
	public IReadOnlyList<string> Branches { get; }

	/// <summary>Whether naming a new branch is offered.</summary>
	public bool AllowCreate { get; }

	/// <summary>Selected existing branch, or <see langword="null"/> when creating one.</summary>
	public string? SelectedBranch
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				OnPropertyChanged(nameof(CanAccept));
			}
		}
	}

	/// <summary>Name for a branch to create; takes precedence over the selection when filled.</summary>
	public string NewBranchName
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

	/// <summary>Whether a branch has been chosen or named.</summary>
	public bool CanAccept => !string.IsNullOrWhiteSpace(SelectedBranch)
		|| AllowCreate && !string.IsNullOrWhiteSpace(NewBranchName);

	/// <summary>Builds the result, or <see langword="null"/> when nothing is chosen.</summary>
	public GitBranchPickDialogResult? CreateResult()
	{
		var newBranch = NewBranchName.Trim();
		if (AllowCreate && !string.IsNullOrWhiteSpace(newBranch))
		{
			return new GitBranchPickDialogResult(newBranch, Create: true);
		}

		var branch = SelectedBranch?.Trim() ?? string.Empty;
		return string.IsNullOrWhiteSpace(branch)
			? null
			: new GitBranchPickDialogResult(branch, Create: false);
	}
}